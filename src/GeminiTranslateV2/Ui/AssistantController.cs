using System.Text;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Session;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Toda a conversa com o assistente, sem nenhuma janela.
/// </summary>
/// <remarks>
/// Existe porque o assistente é acionado de DOIS lugares — a aba na janela principal e a barra
/// flutuante sobre a reunião — e antes cada um tinha a sua cópia de chat, de estado ocupado e de
/// tratamento de erro. Eram duas superfícies com históricos separados para a mesma função. Agora
/// há um histórico só, e as janelas são apenas vistas dele.
/// </remarks>
public sealed class AssistantController(
    IAssistant assistant,
    ConversationContext context,
    IScreenCapture screen)
{
    /// <summary>
    /// Chat do assistente: serve ao mesmo tempo de tela e de histórico mandado à API.
    /// </summary>
    /// <remarks>
    /// As ações de botão também entram aqui, com um turno de usuário sintético descrevendo o que
    /// foi pedido, para que se possa continuar a conversa em cima do resultado ("explica melhor",
    /// "e se eu responder X?") em vez de cada ação ser um fato isolado. É separado do
    /// <see cref="ConversationContext"/>, que é a transcrição da reunião: limpar um não mexe no outro.
    /// </remarks>
    private readonly List<ChatTurn> _chat = [];

    /// <summary>Texto de erro exibido fora do histórico. Ver <see cref="ShowFailure"/>.</summary>
    private string? _failure;

    /// <summary>Uma ação por vez: a cota gratuita é por requisição, não por token.</summary>
    public bool Busy { get; private set; }

    /// <summary>Estado legível da ação em andamento.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Chat inteiro, já formatado para exibição, sempre que ele muda.</summary>
    public event Action<string>? ChatChanged;

    /// <summary>Pedido para a interface se mostrar — o resultado vai aparecer nela.</summary>
    public event Action? ResultReady;

    /// <summary>
    /// Esconde a interface antes de capturar a tela e a devolve depois. Sem isso o próprio app
    /// apareceria no print que a IA vai analisar.
    /// </summary>
    public Func<Func<byte[]>, Task<byte[]>>? CaptureWithUiHidden { get; set; }

    /// <summary>Deixa o usuário arrastar uma região da tela. Devolve vazio se ele cancelar.</summary>
    public Func<Task<System.Drawing.Rectangle>>? SelectRegion { get; set; }

    /// <summary>Analisa um print da tela inteira.</summary>
    public Task CaptureScreenAsync()
    {
        Log.Write("Assistente", "ação: print da tela.");
        return RunAsync("analisando print…", "(print da tela toda)", async ct =>
        {
            byte[] png = await Capture(screen.CaptureFull);
            return await assistant.AnalyzeImageAsync(png, context.GetRecent(), ct);
        });
    }

    /// <summary>Deixa o usuário escolher uma região e a analisa.</summary>
    public async Task CaptureRegionAsync()
    {
        if (Busy || SelectRegion is null) return;

        Log.Write("Assistente", "ação: selecionar região.");
        var region = await SelectRegion();
        if (region.Width <= 0)
        {
            StatusChanged?.Invoke("seleção cancelada");
            return;
        }

        await RunAsync("analisando região…", "(print de uma região da tela)", async ct =>
        {
            byte[] png = screen.Capture(region.X, region.Y, region.Width, region.Height);
            return await assistant.AnalyzeImageAsync(png, context.GetRecent(), ct);
        });
    }

    /// <summary>
    /// Sugere uma resposta para o momento atual.
    /// </summary>
    /// <remarks>
    /// Se a outra pessoa acabou de perguntar algo, responder ESSA pergunta é sempre mais útil que
    /// sugerir falas genéricas — mas ela sozinha costuma ser ambígua, então vai com a conversa
    /// inteira como contexto.
    /// </remarks>
    public async Task SuggestAsync()
    {
        if (Busy) return;

        if (context.IsEmpty)
        {
            Log.Write("Assistente", "ação: sugerir — sem conversa acumulada.");
            StatusChanged?.Invoke("ainda não há conversa suficiente para sugerir");
            ResultReady?.Invoke();
            return;
        }

        var question = context.RecentQuestion;
        if (question is not null)
        {
            Log.Write("Assistente", $"ação: responder a última pergunta — '{question}'");
            await RunAsync("respondendo a última pergunta…", $"(o que respondo a: \"{question}\"?)",
                ct => assistant.SuggestAnswerAsync(question, context.GetRecent(), ct));
            return;
        }

        Log.Write("Assistente", "ação: sugerir da conversa (sem pergunta recente).");
        await RunAsync("pensando na resposta…", "(o que eu posso responder agora?)",
            ct => assistant.SuggestFromConversationAsync(context.GetRecent(), ct));
    }

    /// <summary>Pergunta livre digitada pelo usuário.</summary>
    public Task AskAsync(string question)
    {
        if (Busy || question.Length == 0) return Task.CompletedTask;

        Log.Write("Assistente", $"chat: pergunta do usuário ({question.Length} chars).");
        return RunAsync("pensando…", question,
            ct => assistant.ChatAsync(_chat, context.GetRecent(), ct));
    }

    /// <summary>Esvazia o chat do assistente. Não mexe na transcrição da reunião.</summary>
    public void ClearChat()
    {
        _chat.Clear();
        _failure = null;
        StatusChanged?.Invoke("chat limpo");
        ChatChanged?.Invoke("");
        Log.Write("Assistente", "chat limpo (a transcrição da reunião continua).");
    }

    /// <summary>O chat inteiro, formatado para exibição.</summary>
    public string RenderChat(bool pending = false)
    {
        var text = new StringBuilder();
        foreach (var turn in _chat)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(turn.FromUser ? "Você: " : "IA: ").Append(turn.Text);
        }

        if (pending) text.Append("\n\nIA: …");
        if (_failure is not null) text.Append("\n\n⚠ ").Append(_failure);

        return text.ToString();
    }

    private Task<byte[]> Capture(Func<byte[]> capture) =>
        CaptureWithUiHidden is null ? Task.FromResult(capture()) : CaptureWithUiHidden(capture);

    /// <summary>
    /// Executa uma chamada e registra o par pergunta/resposta no chat.
    /// </summary>
    /// <param name="busyMessage">O que mostrar enquanto a chamada acontece.</param>
    /// <param name="userTurn">
    /// O que aparece como fala do usuário: a pergunta digitada, ou uma descrição da ação para os
    /// botões. Entra no chat ANTES da chamada, porque a API espera o histórico terminando na
    /// pergunta atual.
    /// </param>
    /// <param name="operation">A chamada em si.</param>
    private async Task RunAsync(string busyMessage, string userTurn, Func<CancellationToken, Task<string>> operation)
    {
        if (Busy) return;

        Busy = true;
        _failure = null;
        StatusChanged?.Invoke(busyMessage);

        _chat.Add(new ChatTurn(true, userTurn));
        ChatChanged?.Invoke(RenderChat(pending: true));
        ResultReady?.Invoke();

        try
        {
            var answer = await operation(CancellationToken.None);
            _chat.Add(new ChatTurn(false, answer));
            StatusChanged?.Invoke("pronto");
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
        finally
        {
            Busy = false;
            ChatChanged?.Invoke(RenderChat());
        }
    }

    /// <summary>
    /// Mostra o erro no chat mas o mantém FORA do histórico mandado à API: repetir a pergunta
    /// depois não pode arrastar junto o texto de "limite atingido".
    /// </summary>
    private void ShowFailure(Exception error)
    {
        _chat.RemoveAt(_chat.Count - 1);
        _failure = error.Message;
        StatusChanged?.Invoke("erro");
        Log.Write("Assistente", "falha na ação: " + error);
    }
}
