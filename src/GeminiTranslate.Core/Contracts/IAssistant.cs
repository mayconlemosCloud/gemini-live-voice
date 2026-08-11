namespace GeminiTranslate.Core.Contracts;

/// <summary>Uma fala do chat do assistente.</summary>
/// <param name="FromUser">Separa o que o usuário digitou do que a IA respondeu.</param>
/// <param name="Text">O conteúdo da fala.</param>
public sealed record ChatTurn(bool FromUser, string Text);

/// <summary>
/// Assistente que sugere respostas e analisa prints, sempre com o contexto da conversa.
/// </summary>
public interface IAssistant : IDisposable
{
    /// <summary>Sugere resposta a uma pergunta específica, usando a conversa para desambiguá-la.</summary>
    Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct);

    /// <summary>Analisa a conversa inteira e sugere respostas para o momento atual.</summary>
    Task<string> SuggestFromConversationAsync(string context, CancellationToken ct);

    /// <summary>Conversa livre no chat lateral, com a reunião apenas como pano de fundo.</summary>
    /// <param name="history">Histórico do chat, terminando na pergunta atual.</param>
    /// <param name="context">Transcrição da reunião.</param>
    /// <param name="ct">Cancelamento.</param>
    Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, string context, CancellationToken ct);

    /// <summary>Analisa uma imagem PNG no contexto da conversa.</summary>
    Task<string> AnalyzeImageAsync(byte[] png, string context, CancellationToken ct);
}

/// <summary>Captura da tela, devolvendo PNG em bytes.</summary>
public interface IScreenCapture
{
    /// <summary>Captura todos os monitores.</summary>
    byte[] CaptureFull();

    /// <summary>Captura um retângulo em coordenadas de tela.</summary>
    byte[] Capture(int x, int y, int width, int height);
}
