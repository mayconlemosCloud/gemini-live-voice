using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.Core.Session;

/// <summary>
/// Verifica se a configuração permite iniciar uma sessão, com mensagens que dizem o que fazer.
/// </summary>
public static class SessionValidator
{
    /// <summary>
    /// Lança <see cref="InvalidOperationException"/> descrevendo o primeiro problema encontrado.
    /// </summary>
    /// <param name="settings">Preferências já sincronizadas com a interface.</param>
    /// <param name="source">O que a "Entrada" vai escutar, ou null se nada foi escolhido.</param>
    public static void Validate(Settings settings, AudioSourceChoice? source)
    {
        RequireApiKey(settings);
        RequireSource(source);
        RequireDevices(settings);
        RejectFeedbackLoops(settings, source);
    }

    private static void RequireApiKey(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("informe a API key do Google AI Studio.");
    }

    private static void RequireSource(AudioSourceChoice? source)
    {
        if (source is null)
            throw new InvalidOperationException(
                "escolha o que escutar: um processo (Teams, Chrome...) ou um dispositivo/cabo.");
    }

    private static void RequireDevices(Settings settings)
    {
        if (settings.HeadphonesDeviceId is null || settings.MicDeviceId is null
            || settings.VirtualMicDeviceId is null)
            throw new InvalidOperationException("selecione fone, microfone e microfone virtual.");

        if (settings.VirtualMicDeviceId == settings.HeadphonesDeviceId)
            throw new InvalidOperationException(
                "o microfone virtual precisa ser um dispositivo separado do fone.");
    }

    /// <summary>
    /// Impede as duas formas de realimentação: escutar o próprio fone recapturaria a tradução, e
    /// escutar o cabo do microfone virtual traria a própria voz traduzida de volta como entrada.
    /// </summary>
    private static void RejectFeedbackLoops(Settings settings, AudioSourceChoice? source)
    {
        if (source is not DeviceSourceChoice device) return;

        if (device.DeviceId == settings.HeadphonesDeviceId)
            throw new InvalidOperationException(
                "o dispositivo escutado não pode ser o mesmo fone onde você ouve a tradução — a " +
                "tradução voltaria para a entrada em loop. Use um cabo virtual dedicado.");

        if (device.DeviceId == settings.VirtualMicDeviceId)
            throw new InvalidOperationException(
                "o dispositivo escutado não pode ser o mesmo cabo do microfone virtual — sua " +
                "própria voz traduzida voltaria como Entrada.");
    }
}
