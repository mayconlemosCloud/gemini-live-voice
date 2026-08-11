namespace GeminiTranslate.Core.Signal;

/// <summary>Tamanho do bloco de áudio que percorre todo o pipeline, da captura à rede.</summary>
public static class CaptureChunk
{
    /// <summary>
    /// Duração de um chunk, em milissegundos.
    /// </summary>
    /// <remarks>
    /// É o tamanho que a documentação do live-translate especifica ("send audio in chunks of
    /// 100ms"). Já esteve em 40 ms para cortar atraso e voltou: o modelo analisa prosódia por
    /// bloco, e 40 ms é curto demais para conter o contorno de uma sílaba inteira. Enquanto o
    /// valor real era 100, sobrou um 40 escrito nas contas de backlog, que faziam todo relatório
    /// de atraso de fila sair 2,5× menor do que era de verdade — daí esta constante ser única.
    /// </remarks>
    public const int DurationMs = 100;

    /// <summary>Bytes de um chunk mono PCM16 na taxa informada.</summary>
    public static int Bytes(int sampleRate) => sampleRate / (1000 / DurationMs) * Pcm.BytesPerSample;
}
