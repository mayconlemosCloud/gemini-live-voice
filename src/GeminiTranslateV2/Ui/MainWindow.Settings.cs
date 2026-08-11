using System.Windows.Controls;
using GeminiTranslate.Core.Configuration;
using NAudio.CoreAudioApi;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Parte da janela principal que liga os controles às <see cref="Settings"/>: preencher os
/// combos, aplicar o que estava salvo e recolher o que o usuário escolheu.
/// </summary>
/// <remarks>
/// Separado do resto da janela porque é mapeamento mecânico entre dois formatos, e não lógica de
/// sessão. Mudanças de preferência mexem só aqui.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Volume máximo da voz original: acima disso ela concorre com a tradução.</summary>
    private const double MaxOriginalVolume = 0.5;

    /// <summary>Preenche os combos de fone, microfone e microfone virtual.</summary>
    private void LoadDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        HeadphonesCombo.ItemsSource = AudioDeviceCatalog.RenderDevices(enumerator);
        VirtualMicCombo.ItemsSource = AudioDeviceCatalog.RenderDevices(enumerator);
        MicCombo.ItemsSource = AudioDeviceCatalog.CaptureDevices(enumerator);
    }

    /// <summary>Preenche o combo de origem da Entrada e restaura a escolha salva.</summary>
    private void LoadSources()
    {
        using var enumerator = new MMDeviceEnumerator();

        var options = AudioDeviceCatalog.Sources(enumerator);
        SourceCombo.ItemsSource = options;
        SourceCombo.SelectedItem = FindSavedSource(options);
    }

    /// <summary>
    /// A origem salva. Um dispositivo vence um nome de processo, como em
    /// <see cref="Settings.EntradaDeviceId"/>.
    /// </summary>
    private SourceOption? FindSavedSource(List<SourceOption> options)
    {
        if (!string.IsNullOrEmpty(_settings.EntradaDeviceId))
        {
            var device = options.OfType<DeviceSourceOption>()
                .FirstOrDefault(o => o.Device.Id == _settings.EntradaDeviceId);
            if (device is not null) return device;
        }

        if (string.IsNullOrEmpty(_settings.EntradaProcessName)) return null;

        return options.OfType<ProcessSourceOption>()
            .FirstOrDefault(o => o.Process.ProcessName.Equals(
                _settings.EntradaProcessName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reflete as preferências salvas nos controles.</summary>
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

        VolumeSlider.Value = Math.Clamp(_settings.OriginalVolume, 0, MaxOriginalVolume);
        UpdateVolumeText();

        SelectDevice(HeadphonesCombo, _settings.HeadphonesDeviceId);
        SelectDevice(MicCombo, _settings.MicDeviceId);
        SelectDevice(VirtualMicCombo, _settings.VirtualMicDeviceId);
    }

    /// <summary>Recolhe o estado dos controles nas preferências e grava.</summary>
    private void SaveSettings()
    {
        _settings.ApiKey = ApiKeyBox.Password;
        _settings.AssistantEnabled = AssistantEnabledCheck.IsChecked == true;
        _settings.MakeCablesDefault = MakeDefaultCheck.IsChecked == true;
        _settings.CatchUpEnabled = CatchUpCheck.IsChecked == true;
        _settings.AssistantContext = AssistantContextBox.Text;

        _settings.HeadphonesDeviceId = SelectedDeviceId(HeadphonesCombo);
        _settings.MicDeviceId = SelectedDeviceId(MicCombo);
        _settings.VirtualMicDeviceId = SelectedDeviceId(VirtualMicCombo);

        _settings.EntradaProcessName = (SourceCombo.SelectedItem as ProcessSourceOption)?.Process.ProcessName;
        _settings.EntradaDeviceId = (SourceCombo.SelectedItem as DeviceSourceOption)?.Device.Id;

        _settings.MyLang = ((Language?)MyLangCombo.SelectedItem)?.Code ?? "pt";
        _settings.TheirLang = ((Language?)TheirLangCombo.SelectedItem)?.Code ?? "en";
        _settings.OriginalVolume = VolumeSlider.Value;

        _services.Settings.Save(_settings);
    }

    private static void SelectDevice(ComboBox combo, string? deviceId) =>
        combo.SelectedItem = ((IEnumerable<DeviceItem>)combo.ItemsSource)
            .FirstOrDefault(d => d.Id == deviceId);

    private static string? SelectedDeviceId(ComboBox combo) => (combo.SelectedItem as DeviceItem)?.Id;
}
