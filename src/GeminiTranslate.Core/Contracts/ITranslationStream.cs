namespace GeminiTranslate.Core.Contracts;

/// <summary>
/// Sessão de tradução ao vivo: recebe áudio contínuo e devolve a tradução falada e transcrita.
/// </summary>
/// <remarks>
/// A segmentação de turno é responsabilidade de quem implementa, não de quem chama: o núcleo
/// manda o stream inteiro, silêncio e pausas incluídos, porque é dessa continuidade que sai a
/// prosódia. O único sinal local de pausa é <see cref="EnqueueAudioStreamEnd"/>.
/// </remarks>
public interface ITranslationStream : IDisposable
{
    /// <summary>Chunk de tradução falada recebido.</summary>
    event Action<byte[]>? AudioReceived;

    /// <summary>Transcrição do que entrou, no idioma original.</summary>
    event Action<string>? InputText;

    /// <summary>Transcrição do que saiu traduzido.</summary>
    event Action<string>? OutputText;

    /// <summary>Mudança de estado legível para a interface.</summary>
    event Action<string>? Status;

    /// <summary>
    /// Chunks ainda esperando para ir para a rede. Deve ficar em 0 ou 1: qualquer valor que se
    /// sustente acima disso é atraso criado no cliente, e não pelo modelo.
    /// </summary>
    int OutboxBacklog { get; }

    /// <summary>Abre a sessão. Erros de autenticação devem aparecer aqui, não em segundo plano.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Enfileira um chunk de áudio sem bloquear a thread de captura.</summary>
    void EnqueueAudio(byte[] pcm);

    /// <summary>Avisa que a entrada pausou, para o turno atual ser fechado de forma limpa.</summary>
    void EnqueueAudioStreamEnd();
}
