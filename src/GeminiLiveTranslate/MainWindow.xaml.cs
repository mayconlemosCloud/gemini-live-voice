using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GeminiLiveTranslate.Audio;
using GeminiLiveTranslate.Billing;
using GeminiLiveTranslate.Config;
using GeminiLiveTranslate.Input;
using GeminiLiveTranslate.Logging;
using GeminiLiveTranslate.Translation;
using NAudio.CoreAudioApi;

namespace GeminiLiveTranslate;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TranslationEngine _engine = new();
    private bool _initializing = true;

    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _logTimer;

    private readonly CostMeter _cost = new();
    private readonly DispatcherTimer _fxTimer;
    private long _inTokens, _outTokens, _totalTokens;

    // Global push-to-talk: F8 toggles the mic even when this window is not focused,
    // so you can stay in Meet/Teams. See GlobalKeyHook.
    private readonly GlobalKeyHook _talkHook = new(Key.F8);

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Log.Info("UI", $"Janela criada. Config carregada: key={Log.Mask(_settings.ApiKey)}, " +
                       $"entrada→{_settings.IncomingTargetLang}, saída→{_settings.OutgoingTargetLang}, saídaAtiva={_settings.EnableOutgoing}");

        IncomingLangCombo.ItemsSource = Languages.All;
        OutgoingLangCombo.ItemsSource = Languages.All;

        // Live log: batch lines to the UI to avoid flooding the dispatcher.
        Log.LineWritten += line => _logQueue.Enqueue(line);
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _logTimer.Tick += FlushLog;
        _logTimer.Start();

        WireEngine();
        LoadDevices();
        ApplySettingsToUi();
        _initializing = false;
        UpdateFeedbackWarning();
        UpdateCostDisplay();

        // Live USD→BRL exchange rate: refresh now and every 10 min.
        _fxTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _fxTimer.Tick += (_, _) => _ = RefreshFxAsync();
        _fxTimer.Start();
        _ = RefreshFxAsync();

        // Push-to-talk: hold SPACE (or the Talk button) to unmute your mic.
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        // Global hands-free push-to-talk: F8 toggles the mic from anywhere,
        // so you can stay focused on the Meet/Teams window.
        _talkHook.Pressed += () => Dispatcher.Invoke(ToggleTalking);
        _talkHook.Start();

        Closing += (_, _) =>
        {
            Log.Info("UI", "Fechando janela.");
            _talkHook.Dispose();
            SaveSettings();
            _ = _engine.StopAsync();
        };
    }

    // ---------- Push-to-talk ----------
    private bool _talking;

    // Fired by the global F8 hook: press once to open the mic, press again to close it.
    private void ToggleTalking()
    {
        if (_talking) StopTalking();
        else StartTalking();
    }

    private void StartTalking()
    {
        if (_talking || !_engine.Running || !_engine.HasOutgoing) return;
        _talking = true;
        _ = _engine.OutgoingTalkStartAsync();   // manual-VAD activityStart + unmute
        TalkButton.Content = "🎙️ Falando… (F8 para parar)";
        TalkButton.Background = (Brush)FindResource("Accent");
        TalkButton.Foreground = (Brush)FindResource("AccentText");
    }

    private void StopTalking()
    {
        if (!_talking) return;
        _talking = false;
        _ = _engine.OutgoingTalkEndAsync();      // mute + manual-VAD activityEnd (closes the turn)
        TalkButton.Content = "🎤 Falar: F8 (ou segure ESPAÇO)";
        TalkButton.Background = (Brush)FindResource("Field");
        TalkButton.Foreground = (Brush)FindResource("Text");
    }

    private void OnTalkDown(object sender, MouseButtonEventArgs e) => StartTalking();
    private void OnTalkUp(object sender, MouseButtonEventArgs e) => StopTalking();
    private void OnTalkLeave(object sender, MouseEventArgs e) => StopTalking();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't steal SPACE while typing in a text field.
        if (e.Key != Key.Space || Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox)
            return;
        if (_engine.Running && _engine.HasOutgoing) { StartTalking(); e.Handled = true; }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _talking) { StopTalking(); e.Handled = true; }
    }

    private async Task RefreshFxAsync()
    {
        var rate = await CostMeter.FetchUsdToBrlAsync();
        if (rate is double r)
        {
            _cost.UsdToBrl = r;
            _settings.UsdToBrl = r;
            OnUi(UpdateCostDisplay);
        }
    }

    private void FlushLog(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        while (_logQueue.TryDequeue(out var line))
            sb.AppendLine(line);
        LogBox.AppendText(sb.ToString());

        // Keep the box from growing without bound (~800 lines).
        // LineCount counts wrapped display lines, so it can exceed 800 while the actual
        // '\n'-separated entries are fewer than 600 — guard before slicing or `^600..`
        // resolves to a negative start index and throws ArgumentOutOfRangeException.
        if (LogBox.LineCount > 800)
        {
            var lines = LogBox.Text.Split('\n');
            if (lines.Length > 600)
                LogBox.Text = string.Join("\n", lines[^600..]);
        }
        if (AutoScrollLog.IsChecked == true)
            LogBox.ScrollToEnd();
    }

    // ---------- Conversation recording ----------
    private void OnRecordToggle(object sender, RoutedEventArgs e)
    {
        if (!_engine.Running) return;
        try
        {
            if (_engine.IsRecording)
            {
                var path = _engine.StopRecording();
                SetRecordingUi(false);
                StatusText.Text = path is null ? "Gravação parada." : $"Gravação salva em: {path}";
            }
            else
            {
                var path = _engine.StartRecording();
                SetRecordingUi(true);
                StatusText.Text = $"Gravando a conversa em: {path}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Falha na gravação da conversa", ex);
            StatusText.Text = "Erro ao gravar: " + ex.Message;
        }
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.Content = recording ? "⏹ Parar gravação" : "⏺ Gravar conversa";
        RecordButton.Background = (Brush)FindResource(recording ? "Warn" : "Field");
        RecordButton.Foreground = (Brush)FindResource(recording ? "AccentText" : "Text");
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => Log.OpenFolder();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); StatusText.Text = "Log copiado para a área de transferência."; }
        catch (Exception ex) { Log.Error("UI", "Falha ao copiar log", ex); }
    }

    // ---------- Engine event wiring (marshal to UI thread) ----------
    private void WireEngine()
    {
        _engine.IncomingText += t => OnUi(() => Append(IncomingTranscript, t));
        _engine.OutgoingText += t => OnUi(() => Append(OutgoingTranscript, t));
        _engine.IncomingLevel += l => OnUi(() => IncomingMeter.Value = Math.Min(1, l * 4));
        _engine.OutgoingLevel += l => OnUi(() => OutgoingMeter.Value = Math.Min(1, l * 4));
        _engine.Status += m => OnUi(() => StatusText.Text = m);
        _engine.UsageChanged += (i, o, t) => OnUi(() =>
        {
            _inTokens = i; _outTokens = o; _totalTokens = t;
            UpdateCostDisplay();
        });
    }

    private void OnCostFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _cost.InputUsdPerMillion = ParseDouble(PriceInBox.Text, _cost.InputUsdPerMillion);
        _cost.OutputUsdPerMillion = ParseDouble(PriceOutBox.Text, _cost.OutputUsdPerMillion);
        _settings.InputUsdPerMillion = _cost.InputUsdPerMillion;
        _settings.OutputUsdPerMillion = _cost.OutputUsdPerMillion;
        _settings.BudgetBrl = ParseDouble(BudgetBox.Text, 0);
        UpdateCostDisplay();
    }

    private static double ParseDouble(string? s, double fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Replace(",", ".");
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private void UpdateCostDisplay()
    {
        double usd = _cost.CostUsd(_inTokens, _outTokens);
        double brl = _cost.CostBrl(_inTokens, _outTokens);
        CostText.Text = $"US$ {usd:N4}  ·  R$ {brl:N2}";

        double budget = _settings.BudgetBrl;
        if (budget > 0)
        {
            double pct = brl / budget;
            BudgetBar.Value = Math.Min(1, pct);
            BudgetBar.Foreground = (Brush)FindResource(pct >= 1 ? "Warn" : "Accent");
            BudgetText.Text = $"R$ {brl:N2} de R$ {budget:N2} ({pct:P0}) · tokens {_totalTokens:N0} · US$1={_cost.UsdToBrl:N2}";
        }
        else
        {
            BudgetBar.Value = 0;
            BudgetText.Text = $"Sem orçamento · tokens {_totalTokens:N0} · US$1=R$ {_cost.UsdToBrl:N2}";
        }
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private static void Append(TextBox box, string text)
    {
        box.AppendText(text);
        box.ScrollToEnd();
    }

    private static string NameOf(ComboBox combo) =>
        (combo.SelectedItem as AudioDeviceInfo)?.Name ?? "(nenhum)";

    // ---------- Devices ----------
    private void LoadDevices()
    {
        var render = AudioDeviceService.GetDevices(DataFlow.Render);
        var capture = AudioDeviceService.GetDevices(DataFlow.Capture);

        MeetingDeviceCombo.ItemsSource = render;
        HeadphonesCombo.ItemsSource = new List<AudioDeviceInfo>(render);
        VirtualMicCombo.ItemsSource = new List<AudioDeviceInfo>(render);
        MicCombo.ItemsSource = capture;
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        var prev = _initializing;
        _initializing = true;
        LoadDevices();
        ApplySettingsToUi();
        _initializing = prev;
        UpdateFeedbackWarning();
    }

    // ---------- Settings <-> UI ----------
    private void ApplySettingsToUi()
    {
        ApiKeyBox.Password = _settings.ApiKey;
        EnableOutgoingCheck.IsChecked = _settings.EnableOutgoing;
        ContinuousCheck.IsChecked = _settings.ContinuousStreaming;
        DuckCheck.IsChecked = _settings.DuckOriginal;
        AutoDefaultCheck.IsChecked = _settings.AutoSetDefaultDevice;
        DuckLevelSlider.Value = Math.Clamp(_settings.DuckOriginalLevel, 0, DuckLevelSlider.Maximum);
        UpdateDuckLevelText();
        DuckLevelPanel.IsEnabled = _settings.DuckOriginal;

        _cost.InputUsdPerMillion = _settings.InputUsdPerMillion;
        _cost.OutputUsdPerMillion = _settings.OutputUsdPerMillion;
        _cost.UsdToBrl = _settings.UsdToBrl;
        BudgetBox.Text = _settings.BudgetBrl > 0 ? _settings.BudgetBrl.ToString("0.##", CultureInfo.InvariantCulture) : "";
        PriceInBox.Text = _settings.InputUsdPerMillion.ToString("0.####", CultureInfo.InvariantCulture);
        PriceOutBox.Text = _settings.OutputUsdPerMillion.ToString("0.####", CultureInfo.InvariantCulture);

        SelectDevice(MeetingDeviceCombo, _settings.MeetingOutputDeviceId,
            AudioDeviceService.DefaultDevice(DataFlow.Render)?.Id);
        SelectDevice(HeadphonesCombo, _settings.HeadphonesDeviceId, null);
        SelectDevice(MicCombo, _settings.MicDeviceId,
            AudioDeviceService.DefaultDevice(DataFlow.Capture)?.Id);
        SelectDevice(VirtualMicCombo, _settings.VirtualMicDeviceId, GuessVirtualCableId());

        IncomingLangCombo.SelectedItem = Languages.ByCode(_settings.IncomingTargetLang);
        OutgoingLangCombo.SelectedItem = Languages.ByCode(_settings.OutgoingTargetLang);
    }

    private static void SelectDevice(ComboBox combo, string? savedId, string? fallbackId)
    {
        var items = (IEnumerable<AudioDeviceInfo>)combo.ItemsSource;
        AudioDeviceInfo? match = items.FirstOrDefault(d => d.Id == savedId)
                                 ?? items.FirstOrDefault(d => d.Id == fallbackId);
        combo.SelectedItem = match;
    }

    private static string? GuessVirtualCableId()
    {
        var render = AudioDeviceService.GetDevices(DataFlow.Render);
        var cable = render.FirstOrDefault(d =>
            d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        return cable?.Id;
    }

    private void CollectSettingsFromUi()
    {
        _settings.ApiKey = ApiKeyBox.Password;
        _settings.EnableOutgoing = EnableOutgoingCheck.IsChecked == true;
        _settings.ContinuousStreaming = ContinuousCheck.IsChecked == true;
        _settings.DuckOriginal = DuckCheck.IsChecked == true;
        _settings.AutoSetDefaultDevice = AutoDefaultCheck.IsChecked == true;
        _settings.DuckOriginalLevel = DuckLevelSlider.Value;
        _settings.MeetingOutputDeviceId = (MeetingDeviceCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.HeadphonesDeviceId = (HeadphonesCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.MicDeviceId = (MicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.VirtualMicDeviceId = (VirtualMicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.IncomingTargetLang = ((Language?)IncomingLangCombo.SelectedItem)?.Code ?? "pt";
        _settings.OutgoingTargetLang = ((Language?)OutgoingLangCombo.SelectedItem)?.Code ?? "en";
        _settings.BudgetBrl = ParseDouble(BudgetBox.Text, 0);
        _settings.InputUsdPerMillion = ParseDouble(PriceInBox.Text, _settings.InputUsdPerMillion);
        _settings.OutputUsdPerMillion = ParseDouble(PriceOutBox.Text, _settings.OutputUsdPerMillion);
    }

    private void SaveSettings()
    {
        CollectSettingsFromUi();
        _settings.Save();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateFeedbackWarning();
    }

    // ---------- Ducking level ----------
    private void OnDuckToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DuckLevelPanel.IsEnabled = DuckCheck.IsChecked == true;
    }

    private void OnDuckLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateDuckLevelText();
        // Apply live so the user can tune while listening.
        _engine.IncomingDuckLevel = (float)DuckLevelSlider.Value;
        _settings.DuckOriginalLevel = DuckLevelSlider.Value;
    }

    private void UpdateDuckLevelText() =>
        DuckLevelText.Text = DuckLevelSlider.Value <= 0.001
            ? "original mudo"
            : $"original a {DuckLevelSlider.Value:P0}";

    // ---------- Setup guide ----------
    private void OnShowGuide(object sender, RoutedEventArgs e)
    {
        string meeting = NameOf(MeetingDeviceCombo);
        string headphones = NameOf(HeadphonesCombo);
        string cableRender = NameOf(VirtualMicCombo);
        var virtualId = (VirtualMicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        string cableCapture = virtualId is not null
            ? AudioDeviceService.CaptureCounterpart(virtualId)?.Name ?? "(lado de captura do seu cabo, ex.: CABLE Output)"
            : "(lado de captura do seu cabo, ex.: CABLE Output)";
        bool outgoing = EnableOutgoingCheck.IsChecked == true;
        bool autoDefault = AutoDefaultCheck.IsChecked == true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("COMO ROTEAR O ÁUDIO");
        sb.AppendLine();
        sb.AppendLine("Neste app:");
        sb.AppendLine($"  • Áudio da reunião (capturar de): {meeting}");
        sb.AppendLine($"  • Ouvir a tradução em: {headphones}");
        if (outgoing) sb.AppendLine($"  • Enviar minha voz traduzida para: {cableRender}");
        sb.AppendLine();
        sb.AppendLine("No Teams / Google Meet / WhatsApp / Zoom, nas configurações de áudio:");
        sb.AppendLine($"  • Alto-falante (saída) → {meeting}");
        sb.AppendLine("     (faz o som da reunião entrar no app para ser traduzido)");
        if (outgoing)
        {
            sb.AppendLine($"  • Microfone (entrada) → {cableCapture}");
            sb.AppendLine("     (faz a reunião transmitir a SUA voz já traduzida)");
        }
        else
        {
            sb.AppendLine("  • Microfone (entrada) → seu microfone real (tradução da sua voz está desativada)");
        }
        sb.AppendLine();
        if (autoDefault)
        {
            sb.AppendLine("Como 'Definir dispositivo padrão' está ligado, apps que seguem o padrão do");
            sb.AppendLine("Windows (WhatsApp, normalmente Teams) se ajustam sozinhos ao Iniciar.");
            sb.AppendLine("O Google Meet no navegador NÃO segue o padrão: ajuste o microfone/alto-falante");
            sb.AppendLine("uma vez nas configurações da chamada (o Chrome lembra por site).");
        }
        else
        {
            sb.AppendLine("Dica: ligue 'Definir dispositivo padrão do Windows ao iniciar' para que");
            sb.AppendLine("WhatsApp/Teams se ajustem sozinhos. O Meet no navegador continua manual,");
            sb.AppendLine("mas o Chrome lembra a escolha por site.");
        }
        sb.AppendLine();
        sb.AppendLine("Importante: o 'Alto-falante' da reunião e o cabo da sua voz precisam ser");
        sb.AppendLine("dispositivos DIFERENTES, senão a tradução volta para a entrada e entra em loop.");

        MessageBox.Show(this, sb.ToString(), "Guia de configuração de áudio",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateFeedbackWarning()
    {
        var meeting = (MeetingDeviceCombo.SelectedItem as AudioDeviceInfo)?.Id;
        var headphones = (HeadphonesCombo.SelectedItem as AudioDeviceInfo)?.Id;
        if (meeting != null && meeting == headphones)
        {
            WarningText.Text = "⚠ O áudio da reunião e o seu fone são o mesmo dispositivo. " +
                "A tradução que você ouvir vai ser recapturada e re-traduzida (eco). " +
                "Roteie o som da reunião para um cabo virtual ou use fones separados.";
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Start / Stop ----------
    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_engine.Running)
        {
            Log.Info("UI", "Botão: Parar");
            StartButton.IsEnabled = false;
            await _engine.StopAsync();
            SetRunningUi(false);
            StartButton.IsEnabled = true;
            return;
        }

        SaveSettings();
        Log.Info("UI", "Botão: Iniciar");
        Log.Info("UI", $"Dispositivos — reunião='{NameOf(MeetingDeviceCombo)}', fone='{NameOf(HeadphonesCombo)}', " +
                       $"mic='{NameOf(MicCombo)}', micVirtual='{NameOf(VirtualMicCombo)}'");
        IncomingTranscript.Clear();
        OutgoingTranscript.Clear();
        IncomingHeader.Text = $"A outra pessoa  →  você ouve em {Languages.ByCode(_settings.IncomingTargetLang).Name}";
        OutgoingHeader.Text = $"Você  →  eles ouvem em {Languages.ByCode(_settings.OutgoingTargetLang).Name}";

        StartButton.IsEnabled = false;
        StatusText.Text = "Conectando…";
        try
        {
            await _engine.StartAsync(_settings);
            SetRunningUi(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erro: " + ex.Message;
            StatusText.Foreground = (Brush)FindResource("Warn");
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void SetRunningUi(bool running)
    {
        StartButton.Content = running ? "■  Parar" : "▶  Iniciar";
        StartButton.Background = (Brush)FindResource(running ? "Warn" : "Accent");
        StatusText.Text = running ? "Traduzindo ao vivo…" : "Parado";
        StatusText.Foreground = (Brush)FindResource("Muted");
        foreach (var c in new Control[] { MeetingDeviceCombo, HeadphonesCombo, MicCombo,
                 VirtualMicCombo, IncomingLangCombo, OutgoingLangCombo, EnableOutgoingCheck,
                 ContinuousCheck, DuckCheck, AutoDefaultCheck, RefreshButton })
            c.IsEnabled = !running;

        // Talk button only matters while running with the outgoing direction active.
        if (!running) StopTalking();
        TalkButton.IsEnabled = running && _engine.HasOutgoing;

        // Recording is only meaningful while translating; StopAsync already finalized any .wav.
        RecordButton.IsEnabled = running;
        if (!running) SetRecordingUi(false);
    }
}
