using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Captura do microfone real, na taxa nativa, mono PCM16.
/// </summary>
/// <remarks>
/// Quem limpa o sinal é o Windows, não esta classe: ligue supressão de ruído e cancelamento de
/// eco em Configurações → Sistema → Som → seu microfone → "Aprimorar áudio".
/// </remarks>
public sealed class MicCapture : WasapiAudioSource
{
    /// <summary>
    /// Buffer de captura de 20 ms com sincronismo por evento. O padrão do NAudio é 100 ms, e
    /// esses 80 ms extras entrariam inteiros no atraso que o ouvinte sente.
    /// </summary>
    private const int CaptureBufferMs = 20;

    /// <param name="device">Endpoint de captura do microfone.</param>
    public MicCapture(MMDevice device)
        : base(new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs),
               "Mic", device.FriendlyName)
    {
    }

    /// <summary>
    /// Usa apenas o canal 0.
    /// </summary>
    /// <remarks>
    /// Somar as cápsulas de um arranjo de microfones produz filtragem em pente: elas captam a
    /// mesma voz com atrasos diferentes, e a soma cancela faixas de frequência.
    /// </remarks>
    protected override ISampleProvider ToMono(ISampleProvider source) => new FirstChannelProvider(source);
}
