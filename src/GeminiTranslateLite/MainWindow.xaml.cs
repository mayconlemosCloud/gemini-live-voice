using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;

namespace GeminiTranslateLite;

public sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private Direction? _incoming;
    private Direction? _outgoing;
    private bool Running => _incoming is not null;

    public MainWindow()
    {
        InitializeComponent();
        LoadDevices();
        ApplySettings();
        Closing += (_, _) => { SaveSettings(); StopAll(); };
    }

    // ---------- devices / settings ----------

    private void LoadDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();
        var capture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName)).ToList();

        MeetingCombo.ItemsSource = render;
        HeadphonesCombo.ItemsSource = render.ToList();
        VirtualMicCombo.ItemsSource = render.ToList();
        MicCombo.ItemsSource = capture;
    }

    private void ApplySettings()
    {
        ApiKeyBox.Password = _settings.ApiKey;
        MyLangCombo.ItemsSource = Languages.All;
        TheirLangCombo.ItemsSource = Languages.All;
        MyLangCombo.SelectedItem = Languages.ByCode(_settings.MyLang);
        TheirLangCombo.SelectedItem = Languages.ByCode(_settings.TheirLang);
        VolumeSlider.Value = Math.Clamp(_settings.OriginalVolume, 0, 0.5);
        UpdateVolumeText();

        Select(MeetingCombo, _settings.MeetingDeviceId);
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
        _settings.MeetingDeviceId = IdOf(MeetingCombo);
        _settings.HeadphonesDeviceId = IdOf(HeadphonesCombo);
        _settings.MicDeviceId = IdOf(MicCombo);
        _settings.VirtualMicDeviceId = IdOf(VirtualMicCombo);
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

            StatusText.Text = "Conectando…";
            StartButton.IsEnabled = false;

            _incoming = new Direction("Entrada",
                Dev(_settings.MeetingDeviceId!), loopback: true, Dev(_settings.HeadphonesDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.MyLang, (float)_settings.OriginalVolume);
            Wire(_incoming, IncomingBox);

            _outgoing = new Direction("Saída",
                Dev(_settings.MicDeviceId!), loopback: false, Dev(_settings.VirtualMicDeviceId!),
                _settings.ApiKey, _settings.Model, _settings.TheirLang, (float)_settings.OriginalVolume);
            Wire(_outgoing, OutgoingBox);

            await _incoming.StartAsync();
            await _outgoing.StartAsync();

            IncomingHeader.Text = $"A outra pessoa → você ouve em {Languages.ByCode(_settings.MyLang).Name}";
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
        if (_settings.MeetingDeviceId is null || _settings.HeadphonesDeviceId is null ||
            _settings.MicDeviceId is null || _settings.VirtualMicDeviceId is null)
            throw new InvalidOperationException("selecione os quatro dispositivos de áudio.");
        if (_settings.MeetingDeviceId == _settings.HeadphonesDeviceId)
            throw new InvalidOperationException(
                "o áudio da reunião e o fone são o MESMO dispositivo — a tradução seria recapturada (eco/loop).");
        if (_settings.VirtualMicDeviceId == _settings.MeetingDeviceId ||
            _settings.VirtualMicDeviceId == _settings.HeadphonesDeviceId)
            throw new InvalidOperationException(
                "o microfone virtual precisa ser um dispositivo separado da reunião e do fone (senão a tradução entra em loop).");
    }

    private void Wire(Direction d, TextBox box)
    {
        d.TranslatedText += t => Dispatcher.BeginInvoke(() => { box.AppendText(t); box.ScrollToEnd(); });
        d.Status += s => Dispatcher.BeginInvoke(() => StatusText.Text = s);
    }

    private void StopAll()
    {
        try { _incoming?.Dispose(); } catch { }
        try { _outgoing?.Dispose(); } catch { }
        _incoming = _outgoing = null;
    }

    private void SetUi(bool running)
    {
        StartButton.Content = running ? "■  Parar" : "▶  Iniciar";
        StatusText.Text = running ? "Traduzindo ao vivo…" : "Parado";
        MuteButton.IsEnabled = running;
        MuteButton.Content = "🎙 Mic ligado";
        foreach (var c in new Control[] { MeetingCombo, HeadphonesCombo, MicCombo, VirtualMicCombo,
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
