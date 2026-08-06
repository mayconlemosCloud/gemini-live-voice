using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace GeminiTranslateV2;

public sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ProcessItem(string ProcessName, int Id, string Title)
{
    public override string ToString() => $"{Title} ({ProcessName})";
}

/// <summary>Entrada source: one app's audio via Process Loopback.</summary>
public sealed record SourceProcess(ProcessItem Process)
{
    public override string ToString() => $"Processo: {Process}";
}

/// <summary>Entrada source: a render device/cable via WASAPI loopback (the Lite approach).</summary>
public sealed record SourceDevice(DeviceItem Device)
{
    public override string ToString() => $"Dispositivo: {Device.Name}";
}

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private Direction? _incoming;
    private Direction? _outgoing;
    private ConversationRecorder? _recorder;
    private TranscriptLog? _transcript;
    private AssistantClient? _assistant;
    private QuestionTranscript? _questionTranscript;
    private readonly ConversationContext _context = new();
    private OverlayWindow? _overlay;
    private BalanceWindow? _balance;
    private DefaultDeviceScope? _defaultDevices;
    private bool Running => _incoming is not null;

    // Atalho global para reexibir/esconder a janela — ela não está na barra de tarefas nem no Alt+Tab.
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int HK_TOGGLE_WINDOW = 10;
    public const string ShowHotkeyText = "Ctrl+Shift+0";
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        ApplyStealth(_settings.HideFromScreenShare);
        Stealth.Register(this);
        SourceInitialized += OnSourceInitializedHotkey;
        LoadDevices();
        LoadSources();
        ApplySettings();

        var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        delayTimer.Tick += (_, _) => UpdateDelayText();
        delayTimer.Start();

        Closing += (_, _) => { SaveSettings(); StopAll(); ReleaseHotkey(); };
    }

    // ---------- atalho global de mostrar/esconder ----------

    private void OnSourceInitializedHotkey(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(HotkeyProc);
        // Só dá para trocar WS_EX_TOOLWINDOW depois que o HWND existe; aqui a janela ainda não apareceu.
        Stealth.SetHiddenFromAltTab(this, Stealth.Enabled);
        // '0' = 0x30
        bool ok = RegisterHotKey(handle, HK_TOGGLE_WINDOW, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x30);
        Log.Write("Stealth", $"atalho {ShowHotkeyText} (mostrar/esconder janela) registrado: {ok}");
        if (!ok) StatusText.Text = $"atalho {ShowHotkeyText} em uso por outro app";
    }

    private void ReleaseHotkey()
    {
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HK_TOGGLE_WINDOW); } catch { }
        _hwndSource?.RemoveHook(HotkeyProc);
    }

    private IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HK_TOGGLE_WINDOW)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Esconde a janela se ela está à vista e em foco; caso contrário mostra e traz para frente.</summary>
    private void ToggleWindowVisibility()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void UpdateDelayText()
    {
        if (_incoming is null || _outgoing is null)
        {
            DelayPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DelayPanel.Visibility = Visibility.Visible;

        // Ordem: primeiro a direção que o usuário mais sente (a própria fala saindo traduzida).
        (DelayOutText.Text, DelayOutText.Foreground) = LagFormat.Describe("você", _outgoing);
        (DelayInText.Text, DelayInText.Foreground) = LagFormat.Describe("eles", _incoming);
    }

    // ---------- devices / processes / settings ----------

    private void LoadDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();
        var capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();

        HeadphonesCombo.ItemsSource = render;
        VirtualMicCombo.ItemsSource = render.ToList();
        MicCombo.ItemsSource = capture;
    }

    private void LoadSources()
    {
        var processes = Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Select(p => new ProcessItem(p.ProcessName, p.Id, p.MainWindowTitle))
            .OrderBy(p => p.ProcessName)
            .ToList();

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();

        var items = new List<object>();
        items.AddRange(processes.Select(p => new SourceProcess(p)));
        items.AddRange(devices.Select(d => new SourceDevice(d)));
        SourceCombo.ItemsSource = items;

        // A saved device wins over a saved process name (see Settings.EntradaDeviceId).
        if (!string.IsNullOrEmpty(_settings.EntradaDeviceId))
            SourceCombo.SelectedItem = items.OfType<SourceDevice>()
                .FirstOrDefault(s => s.Device.Id == _settings.EntradaDeviceId);
        if (SourceCombo.SelectedItem is null && !string.IsNullOrEmpty(_settings.EntradaProcessName))
            SourceCombo.SelectedItem = items.OfType<SourceProcess>()
                .FirstOrDefault(s => s.Process.ProcessName.Equals(_settings.EntradaProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnRefreshSources(object sender, RoutedEventArgs e) => LoadSources();

    private void ApplySettings()
    {
        ApiKeyBox.Password = _settings.ApiKey;
        AssistantEnabledCheck.IsChecked = _settings.AssistantEnabled;
        MakeDefaultCheck.IsChecked = _settings.MakeCablesDefault;
        CatchUpCheck.IsChecked = _settings.CatchUpEnabled;
        AssistantContextBox.Text = _settings.AssistantContext;
        MyLangCombo.ItemsSource = Languages.All;
        TheirLangCombo.ItemsSource = Languages.All;
        MyLangCombo.SelectedItem = Languages.ByCode(_settings.MyLang);
        TheirLangCombo.SelectedItem = Languages.ByCode(_settings.TheirLang);
        VolumeSlider.Value = Math.Clamp(_settings.OriginalVolume, 0, 0.5);
        UpdateVolumeText();

        Select(HeadphonesCombo, _settings.HeadphonesDeviceId);
        Select(MicCombo, _settings.MicDeviceId);
        Select(VirtualMicCombo, _settings.VirtualMicDeviceId);
    }

    private static void Select(ComboBox combo, string? id)
    {
        combo.SelectedItem = ((IEnumerable<DeviceItem>)combo.ItemsSource).FirstOrDefault(d => d.Id == id);
    }

    private static string? IdOf(ComboBox combo) => (combo.SelectedItem as DeviceItem)?.Id;

    private void SaveSettings()
    {
        _settings.ApiKey = ApiKeyBox.Password;
        _settings.AssistantEnabled = AssistantEnabledCheck.IsChecked == true;
        _settings.MakeCablesDefault = MakeDefaultCheck.IsChecked == true;
        _settings.CatchUpEnabled = CatchUpCheck.IsChecked == true;
        _settings.AssistantContext = AssistantContextBox.Text;
        _settings.HeadphonesDeviceId = IdOf(HeadphonesCombo);
        _settings.MicDeviceId = IdOf(MicCombo);
        _settings.VirtualMicDeviceId = IdOf(VirtualMicCombo);
        _settings.EntradaProcessName = (SourceCombo.SelectedItem as SourceProcess)?.Process.ProcessName;
        _settings.EntradaDeviceId = (SourceCombo.SelectedItem as SourceDevice)?.Device.Id;
        _settings.MyLang = ((Language?)MyLangCombo.SelectedItem)?.Code ?? "pt";
        _settings.TheirLang = ((Language?)TheirLangCombo.SelectedItem)?.Code ?? "en";
        _settings.OriginalVolume = VolumeSlider.Value;
        _settings.Save();
    }

    // ---------- start / stop ----------

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (Running)
        {
            StopAll();
            SetUi(false);
            return;
        }

        SaveSettings();
        try
        {
            Validate();
            using var enumerator = new MMDeviceEnumerator();
            MMDevice Dev(string id) => enumerator.GetDevice(id);

            IAudioSource entradaSource;
            string entradaLabel;
            if (SourceCombo.SelectedItem is SourceDevice sd)
            {
                entradaSource = new LoopbackCapture(Dev(sd.Device.Id));
                entradaLabel = sd.Device.Name;
            }
            else
            {
                var targetProcess = ((SourceProcess)SourceCombo.SelectedItem).Process;
                // Re-resolve the PID by name at connect time — the process may have restarted
                // since the combo was last refreshed.
                var live = Process.GetProcesses().FirstOrDefault(p =>
                    p.ProcessName.Equals(targetProcess.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && p.MainWindowHandle != IntPtr.Zero)
                    ?? throw new InvalidOperationException($"'{targetProcess.ProcessName}' não está mais rodando — atualize a lista.");
                entradaSource = new ProcessCapture((uint)live.Id);
                entradaLabel = live.ProcessName;
            }

            StatusText.Text = "Conectando…";
            StartButton.IsEnabled = false;

            // Assume os cabos como padrão do Windows para que Teams/WhatsApp/Meet peguem o áudio
            // certo sozinhos. Restaurado em StopAll.
            if (_settings.MakeCablesDefault)
                ApplyDefaultDevices(enumerator, (SourceCombo.SelectedItem as SourceDevice)?.Device.Id);

            // Assistente (IA fora do Google): só liga se marcado e com chave preenchida.
            IncomingBox.Document.Blocks.Clear();
            OutgoingBox.Clear();
            _assistant = null;
            _context.Clear();
            bool wantAssistant = AssistantEnabledCheck.IsChecked == true;
            bool haveKey = !string.IsNullOrWhiteSpace(_settings.ApiKey);
            if (wantAssistant && haveKey)
                _assistant = new AssistantClient(_settings.ApiKey, _settings.AssistantModel, _settings.AssistantContext);
            Log.Write("Assistente", $"configuração: marcado={wantAssistant} · chave={(haveKey ? "ok" : "vazia")} · modelo={_settings.AssistantModel} · ativo={_assistant is not null}");
            _questionTranscript = new QuestionTranscript(IncomingBox, _assistant is null ? null : OpenSuggestion);

            // Overlay flutuante com atalhos globais (só quando o assistente está ativo).
            if (_assistant is not null)
            {
                _overlay = new OverlayWindow(_assistant, _context);
                _overlay.Closed += (_, _) => _overlay = null;
                _overlay.Show();
            }

            _incoming = new Direction("Entrada",
                entradaSource, Dev(_settings.HeadphonesDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.MyLang, (float)_settings.OriginalVolume,
                _settings.CatchUpEnabled);
            WireIncoming(_incoming);

            _outgoing = new Direction("Saída",
                new MicCapture(Dev(_settings.MicDeviceId!)), Dev(_settings.VirtualMicDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.TheirLang, (float)_settings.OriginalVolume,
                _settings.CatchUpEnabled);
            Wire(_outgoing, OutgoingBox);

            // Full conversation log: one stereo .wav (esq = o que você ouviu, dir = o que eles
            // ouviram, ambos com o original por debaixo, idêntico ao áudio ao vivo) + um .txt
            // com original e tradução das duas direções.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _recorder = new ConversationRecorder(System.IO.Path.Combine(Log.Folder, $"conversa-{stamp}.wav"),
                _incoming.OutputMixFormat, _outgoing.OutputMixFormat);
            _incoming.OutputTap = _recorder.WriteIncoming;
            _outgoing.OutputTap = _recorder.WriteOutgoing;
            _transcript = new TranscriptLog(System.IO.Path.Combine(Log.Folder, $"conversa-{stamp}.txt"));
            WireTranscript(_incoming, "Eles");
            WireTranscript(_outgoing, "Você");

            await _incoming.StartAsync();
            await _outgoing.StartAsync();

            // Etiqueta de saldo sempre-no-topo. Independe do assistente: a janela principal fica
            // minimizada durante a chamada, e é justamente aí que se quer saber o que falta sair.
            _balance = new BalanceWindow(_settings);
            _balance.Closed += (_, _) => _balance = null;
            _balance.Show();
            _balance.Bind(_outgoing, _incoming);

            IncomingHeader.Text = $"{entradaLabel} → você ouve em {Languages.ByCode(_settings.MyLang).Name}";
            OutgoingHeader.Text = $"Você → eles ouvem em {Languages.ByCode(_settings.TheirLang).Name}";
            SetUi(true);
        }
        catch (Exception ex)
        {
            StopAll();
            SetUi(false);
            StatusText.Text = "Erro: " + ex.Message;
            Log.Write("UI", "falha ao iniciar: " + ex);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Assume o controle dos dispositivos padrão do Windows: a saída padrão vira o cabo que
    /// escutamos (os apps tocam ali e a tradução ouve) e a entrada padrão vira o lado de captura
    /// do cabo do microfone virtual (os apps ouvem sua voz já traduzida). Assim não é preciso
    /// escolher nada dentro do Teams/WhatsApp.
    /// </summary>
    private void ApplyDefaultDevices(MMDeviceEnumerator enumerator, string? entradaDeviceId)
    {
        string? captureId = null;
        var notes = new List<string>();

        // Lado de captura do cabo do microfone virtual (ex.: "CABLE Input" → "CABLE Output").
        try
        {
            using var virtualMic = enumerator.GetDevice(_settings.VirtualMicDeviceId!);
            using var counterpart = DefaultAudioDevices.FindCaptureCounterpart(enumerator, virtualMic);
            if (counterpart is not null) captureId = counterpart.ID;
            else notes.Add($"não achei o lado de gravação de \"{virtualMic.FriendlyName}\" — escolha o mic manualmente no app de chamada.");
        }
        catch (Exception ex)
        {
            notes.Add("não consegui resolver o microfone virtual: " + ex.Message);
        }

        if (entradaDeviceId is null)
            notes.Add("a saída padrão não foi alterada porque a Entrada é um processo, não um cabo.");

        if (entradaDeviceId is null && captureId is null)
        {
            DefaultDevicesText.Text = "Padrão do Windows: " + string.Join(" ", notes);
            return;
        }

        try
        {
            _defaultDevices = DefaultDeviceScope.Create(entradaDeviceId, captureId);
            var applied = new List<string>();
            if (entradaDeviceId is not null) applied.Add($"saída → {NameOf(enumerator, entradaDeviceId)}");
            if (captureId is not null) applied.Add($"entrada → {NameOf(enumerator, captureId)}");
            DefaultDevicesText.Text = "Padrão do Windows: " + string.Join(" · ", applied)
                + (notes.Count > 0 ? " · " + string.Join(" ", notes) : "");
        }
        catch (Exception ex)
        {
            // Não é motivo para abortar a tradução — só significa configurar na mão no app de chamada.
            _defaultDevices = null;
            DefaultDevicesText.Text = "Padrão do Windows: falhou (" + ex.Message + ") — configure entrada/saída no app de chamada.";
            Log.Write("Padrão", "falha ao trocar dispositivos padrão: " + ex);
        }
    }

    private static string NameOf(MMDeviceEnumerator enumerator, string id)
    {
        try { using var d = enumerator.GetDevice(id); return d.FriendlyName; } catch { return id; }
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException("informe a API key do Google AI Studio.");
        if (SourceCombo.SelectedItem is null)
            throw new InvalidOperationException("escolha o que escutar: um processo (Teams, Chrome...) ou um dispositivo/cabo.");
        if (_settings.HeadphonesDeviceId is null || _settings.MicDeviceId is null || _settings.VirtualMicDeviceId is null)
            throw new InvalidOperationException("selecione fone, microfone e microfone virtual.");
        if (_settings.VirtualMicDeviceId == _settings.HeadphonesDeviceId)
            throw new InvalidOperationException("o microfone virtual precisa ser um dispositivo separado do fone.");
        // Loopback on the same device you listen on would recapture the translation itself
        // (and the original underneath) — an endless feedback loop into the model.
        if (_settings.EntradaDeviceId is not null && _settings.EntradaDeviceId == _settings.HeadphonesDeviceId)
            throw new InvalidOperationException("o dispositivo escutado não pode ser o mesmo fone onde você ouve a tradução — a tradução voltaria para a entrada em loop. Use um cabo virtual dedicado.");
        if (_settings.EntradaDeviceId is not null && _settings.EntradaDeviceId == _settings.VirtualMicDeviceId)
            throw new InvalidOperationException("o dispositivo escutado não pode ser o mesmo cabo do microfone virtual — sua própria voz traduzida voltaria como Entrada.");
    }

    private void Wire(Direction d, TextBox box)
    {
        d.TranslatedText += t =>
        {
            _context.Add("Você", t);
            Dispatcher.BeginInvoke(() =>
            {
                box.AppendText(t);
                box.ScrollToEnd();
            });
        };
        d.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
    }

    /// <summary>Entrada usa RichTextBox com detecção de perguntas (via QuestionTranscript).</summary>
    private void WireIncoming(Direction d)
    {
        d.TranslatedText += t =>
        {
            _context.Add("Eles", t);
            Dispatcher.BeginInvoke(() => _questionTranscript?.Append(t));
        };
        d.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
    }

    private readonly Dictionary<string, SuggestionWindow> _suggestionWindows = new();

    /// <summary>Abre a janela de sugestão e busca a resposta na IA (só ao clicar — poupa custo).</summary>
    private async void OpenSuggestion(string question, string context)
    {
        if (_assistant is null)
        {
            Log.Write("Sugestão", "clique ignorado: assistente desligado (_assistant == null).");
            return;
        }

        // Já tem janela aberta para essa pergunta? Traz para frente em vez de duplicar.
        if (_suggestionWindows.TryGetValue(question, out var existing))
        {
            Log.Write("Sugestão", "janela já aberta — trazendo para frente.");
            existing.Activate();
            return;
        }

        Log.Write("Sugestão", $"abrindo janela para: '{question}'");
        var win = new SuggestionWindow(question) { Owner = this };
        _suggestionWindows[question] = win;
        win.Closed += (_, _) =>
        {
            _suggestionWindows.Remove(question);
            Log.Write("Sugestão", "janela fechada.");
        };
        win.Show();
        Log.Write("Sugestão", "janela exibida (Show). Chamando a IA…");
        try
        {
            var answer = await _assistant.SuggestAnswerAsync(question, context, CancellationToken.None);
            win.SetAnswer(answer);
            Log.Write("Sugestão", "resposta preenchida na janela.");
        }
        catch (Exception ex)
        {
            win.SetError(ex.Message);
            Log.Write("Sugestão", "falha ao sugerir resposta: " + ex);
        }
    }

    private void WireTranscript(Direction d, string who)
    {
        d.OriginalText += t => _transcript?.Append($"{who} [original]", t);
        d.TranslatedText += t => _transcript?.Append($"{who} [tradução]", t);
    }

    private void StopAll()
    {
        try { _overlay?.Close(); } catch { }
        try { _balance?.Close(); } catch { }
        try { _incoming?.Dispose(); } catch { }
        try { _outgoing?.Dispose(); } catch { }
        try { _recorder?.Dispose(); } catch { }
        try { _transcript?.Dispose(); } catch { }
        try { _assistant?.Dispose(); } catch { }
        // Devolve os dispositivos padrão do Windows como estavam antes do Iniciar.
        try { _defaultDevices?.Dispose(); } catch (Exception ex) { Log.Write("Padrão", "falha ao restaurar: " + ex); }
        _defaultDevices = null;
        _overlay = null;
        _balance = null;
        _incoming = _outgoing = null;
        _recorder = null;
        _transcript = null;
        _assistant = null;
    }

    private void SetUi(bool running)
    {
        StartButton.Content = running ? "■  Parar" : "▶  Iniciar";
        StatusText.Text = running ? "Traduzindo ao vivo…" : "Parado";
        MuteButton.IsEnabled = running;
        MuteButton.Content = "🎙 Mic ligado";
        foreach (var c in new Control[] { SourceCombo, RefreshSourcesButton, HeadphonesCombo, MicCombo, VirtualMicCombo,
                 MyLangCombo, TheirLangCombo, ApiKeyBox, AssistantEnabledCheck, AssistantContextBox, MakeDefaultCheck,
                 CatchUpCheck })
            c.IsEnabled = !running;
        if (!running) DefaultDevicesText.Text = "";
    }

    // ---------- live controls ----------

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_outgoing is null) return;
        _outgoing.Muted = !_outgoing.Muted;
        MuteButton.Content = _outgoing.Muted ? "🔇 Mic mudo" : "🎙 Mic ligado";
    }

    private void OnStealthToggle(object sender, RoutedEventArgs e) =>
        ApplyStealth(!Stealth.Enabled);

    /// <summary>Liga/desliga a ocultação em todas as janelas do app e reflete no botão.</summary>
    private void ApplyStealth(bool on)
    {
        Stealth.SetEnabled(on);
        // Sem isso a janela some da captura, mas a miniatura que o Alt+Tab desenha ainda vaza.
        Stealth.SetHiddenFromAltTab(this, on);
        _settings.HideFromScreenShare = on;
        StealthButton.Content = on ? "🕶 Oculto na tela" : "👁 Visível na tela";
        StealthButton.ToolTip = on
            ? $"Ligado: o app não aparece em compartilhamento de tela, gravação, print nem no Alt+Tab.\n{ShowHotkeyText} mostra/esconde esta janela. Clique para deixá-la visível."
            : "Desligado: o app aparece normalmente para quem vê sua tela e volta ao Alt+Tab. Clique para ocultar.";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        UpdateVolumeText();
        float v = (float)VolumeSlider.Value;
        if (_incoming is not null) _incoming.OriginalVolume = v;
        if (_outgoing is not null) _outgoing.OriginalVolume = v;
        _settings.OriginalVolume = v;
    }

    private void UpdateVolumeText() => VolumeText.Text = $"{VolumeSlider.Value:P0}";
}
