namespace GeminiTranslate.Core.Signal;

/// <summary>Taxas de amostragem fixas do pipeline.</summary>
/// <remarks>
/// Ficam juntas porque vários medidores comparam durações entre os dois lados, e uma divergência
/// entre eles desalinharia silenciosamente as contagens de fala.
/// </remarks>
public static class AudioRates
{
    /// <summary>
    /// Taxa de entrada documentada da Live API, e a que o áudio é convertido antes de ir à rede.
    /// </summary>
    /// <remarks>
    /// Não é cosmético. A 48 kHz cada direção envia cerca de 96 KB/s de PCM, que viram 128 KB/s
    /// depois do base64 — as duas direções somam cerca de 2 Mbps de upload contínuo, competindo
    /// com o app de reunião no mesmo link. Quando o uplink não dá conta, a fila de envio cresce
    /// em silêncio e o atraso vira permanente, porque o servidor devolve o dub em 1× tempo real
    /// e nada do que atrasou é recuperado. A 16 kHz o mesmo áudio ocupa um terço disso, e não se
    /// perde nada: a API reamostra para 16 kHz do lado dela de qualquer forma.
    /// </remarks>
    public const int Wire = 16000;

    /// <summary>Taxa em que o modelo devolve a tradução falada.</summary>
    public const int Dub = 24000;
}
