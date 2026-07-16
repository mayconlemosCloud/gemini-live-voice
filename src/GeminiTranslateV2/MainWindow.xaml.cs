using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private bool Running => _incoming is not null;

    public MainWindow()
    {
        InitializeComponent();
        LoadDevices();
        LoadSources();
        ApplySettings();

        var delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        delayTimer.Tick += (_, _) => UpdateDelayText();
        delayTimer.Start();

        Closing += (_, _) => { SaveSettings(); StopAll(); };
    }

    private void UpdateDelayText()
    {
        if (_incoming is null || _outgoing is null)
        {
            DelayText.Text = "";
            return;
        }
        static string Part(string name, Direction d) =>
            $"{name} {d.TranslationQueue.TotalSeconds:0.0}s{(d.CatchingUp ? " ⏩" : "")}";
        DelayText.Text = $"fila: {Part("entrada", _incoming)} · {Part("saída", _outgoing)}";
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

            _incoming = new Direction("Entrada",
                entradaSource, Dev(_settings.HeadphonesDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.MyLang, (float)_settings.OriginalVolume);
            Wire(_incoming, IncomingBox);

            _outgoing = new Direction("Saída",
                new MicCapture(Dev(_settings.MicDeviceId!)), Dev(_settings.VirtualMicDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.TheirLang, (float)_settings.OriginalVolume);
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
        d.TranslatedText += t => Dispatcher.BeginInvoke(() =>
        {
            box.AppendText(t);
            box.ScrollToEnd();
        });
        d.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
    }

    private void WireTranscript(Direction d, string who)
    {
        d.OriginalText += t => _transcript?.Append($"{who} [original]", t);
        d.TranslatedText += t => _transcript?.Append($"{who} [tradução]", t);
    }

    private void StopAll()
    {
        try { _incoming?.Dispose(); } catch { }
        try { _outgoing?.Dispose(); } catch { }
        try { _recorder?.Dispose(); } catch { }
        try { _transcript?.Dispose(); } catch { }
        _incoming = _outgoing = null;
        _recorder = null;
        _transcript = null;
    }

    private void SetUi(bool running)
    {
        StartButton.Content = running ? "■  Parar" : "▶  Iniciar";
        StatusText.Text = running ? "Traduzindo ao vivo…" : "Parado";
        MuteButton.IsEnabled = running;
        MuteButton.Content = "🎙 Mic ligado";
        foreach (var c in new Control[] { SourceCombo, RefreshSourcesButton, HeadphonesCombo, MicCombo, VirtualMicCombo,
                 MyLangCombo, TheirLangCombo, ApiKeyBox })
            c.IsEnabled = !running;
        IncomingBox.Clear();
        OutgoingBox.Clear();
    }

    // ---------- live controls ----------

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_outgoing is null) return;
        _outgoing.Muted = !_outgoing.Muted;
        MuteButton.Content = _outgoing.Muted ? "🔇 Mic mudo" : "🎙 Mic ligado";
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
