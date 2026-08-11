namespace GeminiTranslate.Core.Contracts;

/// <summary>Formato de um fluxo de áudio, sem depender de nenhuma biblioteca de áudio.</summary>
/// <param name="SampleRate">Amostras por segundo.</param>
/// <param name="Channels">Número de canais.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels);

/// <summary>
/// Leitura de amostras em ponto flutuante, na forma que os algoritmos do núcleo consomem.
/// </summary>
/// <remarks>
/// Espelha o contrato de leitura das bibliotecas de áudio sem importá-las: é o que permite o
/// WSOLA viver no núcleo e ser testado com um gerador de onda em memória.
/// </remarks>
public interface ISampleReader
{
    /// <summary>Lê até <paramref name="count"/> amostras e devolve quantas foram entregues.</summary>
    int Read(float[] buffer, int offset, int count);
}

/// <summary>Converte chunks da taxa nativa de captura para a taxa esperada pela rede.</summary>
public interface IResampler
{
    /// <summary>
    /// Recebe um chunk na taxa nativa e devolve o convertido, ou null enquanto não há amostras
    /// suficientes para um bloco. Guarda estado: alimente na ordem dos chunks capturados.
    /// </summary>
    byte[]? Feed(byte[] chunk);
}

/// <summary>
/// Saída de áudio de uma direção: toca a tradução com a voz original misturada por baixo.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Tradução esperando para tocar — o atraso ao vivo que o ouvinte sente.</summary>
    TimeSpan TranslationQueue { get; }

    /// <summary>Velocidade de reprodução agora. 1,0 significa sem aceleração.</summary>
    double CatchUpSpeed { get; }

    /// <summary>Formato do mix final renderizado.</summary>
    AudioFormat MixFormat { get; }

    /// <summary>Volume da voz original tocada por baixo da tradução, de 0 a 1.</summary>
    float OriginalVolume { set; }

    /// <summary>
    /// Recebe cada bloco realmente tocado — exatamente o que o ouvinte ouve. Null desliga.
    /// </summary>
    Action<float[], int, int>? RenderTap { set; }

    /// <summary>Começa a tocar.</summary>
    void Start();

    /// <summary>Enfileira um chunk de tradução recém-chegado do modelo.</summary>
    void EnqueueTranslation(byte[] pcm);

    /// <summary>Enfileira um chunk da voz original capturada.</summary>
    void EnqueueOriginal(byte[] pcm);
}
