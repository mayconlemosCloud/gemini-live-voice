namespace GeminiTranslate.Core.Contracts;

/// <summary>
/// Contrato comum das fontes de captura (microfone real, loopback de dispositivo, loopback de
/// processo), para que a sessão de tradução as trate de forma idêntica.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Taxa dos chunks emitidos. É a taxa nativa da placa — nunca reamostrada aqui.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Chunk mono PCM16 exatamente como capturado, emitido incondicionalmente.
    /// </summary>
    /// <remarks>
    /// Não existe gate de silêncio local: o modelo segmenta os turnos sozinho a partir do stream
    /// contínuo, e cortar silêncio aqui apagaria a prosódia que ele usa.
    /// </remarks>
    event Action<byte[]>? ChunkAvailable;

    /// <summary>Nível RMS (0..1), cerca de 10 vezes por segundo, para o medidor da interface.</summary>
    event Action<float>? Level;

    /// <summary>Quando true, os chunks continuam sendo medidos mas não são emitidos.</summary>
    bool Muted { get; set; }

    /// <summary>Abre o dispositivo e começa a emitir chunks.</summary>
    Task StartAsync();
}
