using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GeminiLiveTranslate.Audio;
using GeminiLiveTranslate.Billing;
using GeminiLiveTranslate.Config;
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

        Closing += (_, _) => { Log.Info("UI", "Fechando janela."); SaveSettings(); _ = _engine.StopAsync(); };
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
        if (LogBox.LineCount > 800)
        {
            var lines = LogBox.Text.Split('\n');
            LogBox.Text = string.Join("\n", lines[^600..]);
        }
        if (AutoScrollLog.IsChecked == true)
            LogBox.ScrollToEnd();
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
                 ContinuousCheck, DuckCheck, RefreshButton })
            c.IsEnabled = !running;
    }
}
