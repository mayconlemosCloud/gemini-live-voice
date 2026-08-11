using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Infrastructure.Windows;
using NAudio.CoreAudioApi;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>
/// Assume o controle dos dispositivos padrão do Windows durante a sessão.
/// </summary>
/// <remarks>
/// A saída padrão vira o cabo que escutamos — os apps tocam ali e a tradução ouve — e a entrada
/// padrão vira o lado de captura do cabo do microfone virtual, de modo que os apps ouçam a voz
/// já traduzida. Assim não é preciso escolher nada dentro do Teams, do WhatsApp ou do Meet.
///
/// Falhar aqui nunca aborta a tradução: significa apenas configurar na mão no app de chamada.
/// </remarks>
public static class WindowsDefaultDevices
{
    /// <summary>Aplica os padrões possíveis e descreve o que aconteceu.</summary>
    /// <param name="enumerator">Enumerador já aberto.</param>
    /// <param name="settings">Preferências, para achar o microfone virtual.</param>
    /// <param name="entradaDeviceId">Cabo escutado, ou null quando a Entrada é um processo.</param>
    public static DefaultDevicesResult Apply(
        MMDeviceEnumerator enumerator, Settings settings, string? entradaDeviceId)
    {
        var notes = new List<string>();
        string? captureId = ResolveVirtualMicCaptureSide(enumerator, settings, notes);

        if (entradaDeviceId is null)
            notes.Add("a saída padrão não foi alterada porque a Entrada é um processo, não um cabo.");

        if (entradaDeviceId is null && captureId is null)
            return new DefaultDevicesResult(null, "Padrão do Windows: " + string.Join(" ", notes));

        return Apply(enumerator, entradaDeviceId, captureId, notes);
    }

    /// <summary>
    /// Acha o lado de captura do cabo do microfone virtual, por exemplo "CABLE Input" levando a
    /// "CABLE Output".
    /// </summary>
    private static string? ResolveVirtualMicCaptureSide(
        MMDeviceEnumerator enumerator, Settings settings, List<string> notes)
    {
        try
        {
            using var virtualMic = enumerator.GetDevice(settings.VirtualMicDeviceId!);
            using var counterpart = DefaultAudioDevices.FindCaptureCounterpart(enumerator, virtualMic);

            if (counterpart is not null) return counterpart.ID;

            notes.Add($"não achei o lado de gravação de \"{virtualMic.FriendlyName}\" — escolha o " +
                      "mic manualmente no app de chamada.");
        }
        catch (Exception ex)
        {
            notes.Add("não consegui resolver o microfone virtual: " + ex.Message);
        }
        return null;
    }

    private static DefaultDevicesResult Apply(
        MMDeviceEnumerator enumerator, string? renderId, string? captureId, List<string> notes)
    {
        try
        {
            var scope = DefaultDeviceScope.Create(renderId, captureId);

            var applied = new List<string>();
            if (renderId is not null) applied.Add($"saída → {NameOf(enumerator, renderId)}");
            if (captureId is not null) applied.Add($"entrada → {NameOf(enumerator, captureId)}");

            string note = "Padrão do Windows: " + string.Join(" · ", applied)
                          + (notes.Count > 0 ? " · " + string.Join(" ", notes) : "");
            return new DefaultDevicesResult(scope, note);
        }
        catch (Exception ex)
        {
            Log.Write("Padrão", "falha ao trocar dispositivos padrão: " + ex);
            return new DefaultDevicesResult(null,
                $"Padrão do Windows: falhou ({ex.Message}) — configure entrada/saída no app de chamada.");
        }
    }

    private static string NameOf(MMDeviceEnumerator enumerator, string deviceId)
    {
        try
        {
            using var device = enumerator.GetDevice(deviceId);
            return device.FriendlyName;
        }
        catch
        {
            return deviceId;
        }
    }
}
