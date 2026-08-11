using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GeminiTranslate.App.Ui;

/// <summary>Enumera o que pode aparecer nos combos da janela principal.</summary>
public static class AudioDeviceCatalog
{
    /// <summary>Endpoints de reprodução ativos: fones, alto-falantes e cabos virtuais.</summary>
    public static List<DeviceItem> RenderDevices(MMDeviceEnumerator enumerator) =>
        Enumerate(enumerator, DataFlow.Render);

    /// <summary>Endpoints de captura ativos: microfones e lados de gravação de cabos.</summary>
    public static List<DeviceItem> CaptureDevices(MMDeviceEnumerator enumerator) =>
        Enumerate(enumerator, DataFlow.Capture);

    private static List<DeviceItem> Enumerate(MMDeviceEnumerator enumerator, DataFlow flow) =>
        enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName))
            .ToList();

    /// <summary>
    /// Origens possíveis para a Entrada: primeiro os aplicativos com janela visível, depois os
    /// dispositivos de reprodução.
    /// </summary>
    public static List<SourceOption> Sources(MMDeviceEnumerator enumerator)
    {
        var options = new List<SourceOption>();
        options.AddRange(VisibleProcesses().Select(p => new ProcessSourceOption(p)));
        options.AddRange(RenderDevices(enumerator).Select(d => new DeviceSourceOption(d)));
        return options;
    }

    /// <summary>Processos com janela principal e título — os que plausivelmente tocam áudio de reunião.</summary>
    private static List<ProcessItem> VisibleProcesses() =>
        Process.GetProcesses()
            .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
            .Select(p => new ProcessItem(p.ProcessName, p.Id, p.MainWindowTitle))
            .OrderBy(p => p.ProcessName)
            .ToList();
}
