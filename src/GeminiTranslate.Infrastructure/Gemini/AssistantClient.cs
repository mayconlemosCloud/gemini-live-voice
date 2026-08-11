using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.Infrastructure.Gemini;

/// <summary>
/// Assistente sobre a API generateContent do Gemini: sugere respostas e analisa prints, sempre
/// com o contexto da conversa. Reutiliza a mesma chave usada na tradução.
/// </summary>
/// <remarks>
/// Cobre quatro modos: responder uma pergunta específica, sugerir a partir da conversa inteira,
/// conversar livremente no chat lateral e analisar uma imagem.
/// </remarks>
public sealed class AssistantClient : IAssistant
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int MaxOutputTokens = 1024;
    private const double Temperature = 0.7;
    private const int LogBodyChars = 500;
    private const int LogMessageChars = 200;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _persona;

    /// <param name="apiKey">Chave do Google AI Studio.</param>
    /// <param name="model">Modelo generateContent a usar.</param>
    /// <param name="persona">Contexto opcional sobre o usuário.</param>
    public AssistantClient(string apiKey, string model, string persona)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model;
        _persona = persona?.Trim() ?? "";
    }

    /// <summary>
    /// Sugere resposta a UMA pergunta específica: o trecho clicado, ou a última pergunta detectada.
    /// </summary>
    /// <remarks>
    /// A pergunta sozinha costuma ser ambígua ("e isso aí?", "quanto tempo leva?"), então o modelo
    /// recebe a transcrição dos dois lados para entender do que se trata — mas responde só essa
    /// pergunta.
    /// </remarks>
    public Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct)
    {
        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            user.Append("Transcrição da conversa até agora (para você entender o assunto):\n")
                .Append(context.Trim())
                .Append("\n\n");
        user.Append("Pergunta atual, que é a única que devo responder:\n")
            .Append(question.Trim())
            .Append("\n\nSugira o que eu posso responder a essa pergunta.");

        return CallSingleTurnAsync(AssistantPrompts.AnswerQuestion(_persona), TextParts(user.ToString()), ct);
    }

    /// <summary>Analisa a conversa inteira e sugere possíveis respostas para o momento atual.</summary>
    public Task<string> SuggestFromConversationAsync(string context, CancellationToken ct)
    {
        var user = "Transcrição da conversa:\n" + (context?.Trim() ?? "(vazia)") +
                   "\n\nSugira o que eu posso responder agora.";

        return CallSingleTurnAsync(AssistantPrompts.SuggestFromConversation(_persona), TextParts(user), ct);
    }

    /// <summary>
    /// Pergunta livre digitada pelo usuário, com o histórico do próprio chat.
    /// </summary>
    /// <param name="history">Histórico completo, terminando na pergunta atual.</param>
    /// <param name="context">Transcrição da reunião, usada apenas como pano de fundo.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <remarks>
    /// Diferente dos outros modos, aqui o usuário conduz: pode pedir esclarecimento, mudar de
    /// assunto ou continuar de onde parou, então o histórico vai inteiro.
    /// </remarks>
    public Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, string context, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var turn in history)
            contents.Add(new JsonObject
            {
                ["role"] = turn.FromUser ? "user" : "model",
                ["parts"] = TextParts(turn.Text)
            });

        return CallAsync(AssistantPrompts.Chat(_persona, context), contents, ct);
    }

    /// <summary>Analisa uma imagem PNG no contexto da conversa.</summary>
    public Task<string> AnalyzeImageAsync(byte[] png, string context, CancellationToken ct)
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = "image/png",
                    ["data"] = Convert.ToBase64String(png)
                }
            },
            new JsonObject
            {
                ["text"] = "Contexto recente da conversa:\n" + (context?.Trim() ?? "(vazio)") +
                           "\n\nAnalise o print acima e me ajude."
            }
        };

        return CallSingleTurnAsync(AssistantPrompts.AnalyzeImage(_persona), parts, ct);
    }

    private static JsonArray TextParts(string text) => [new JsonObject { ["text"] = text }];

    /// <summary>Um único turno de usuário — a forma dos modos de sugestão e de imagem.</summary>
    private Task<string> CallSingleTurnAsync(string system, JsonArray userParts, CancellationToken ct) =>
        CallAsync(system, [new JsonObject { ["role"] = "user", ["parts"] = userParts }], ct);

    private async Task<string> CallAsync(string system, JsonArray contents, CancellationToken ct)
    {
        RejectWhileQuotaBlocked();

        for (int attempt = 0; ; attempt++)
        {
            var (status, rawBody, root) = await PostAsync(system, contents, ct);

            if (status == 429)
            {
                await HandleQuotaAsync(root, rawBody, attempt, ct);
                continue;
            }

            if (status is < 200 or >= 300)
            {
                var apiMessage = root?["error"]?["message"]?.GetValue<string>();
                Log.Write("Assistente", "erro da API: " + (apiMessage ?? $"HTTP {status}"));
                throw new InvalidOperationException(apiMessage ?? $"erro HTTP {status} da API do Gemini.");
            }

            return ReadAnswer(root);
        }
    }

    /// <summary>
    /// Barra a chamada enquanto uma espera de cota está ativa.
    /// </summary>
    /// <remarks>
    /// Uma chamada mandada DENTRO da janela de espera de um 429 anterior não tem chance de dar
    /// certo: ela volta 429 e ainda mantém o balde de quota vazio por mais tempo. Nos logs esse
    /// foi o padrão dominante — um 429 seguido de quatro ou cinco recliques em 12 s, todos 429.
    /// </remarks>
    private static void RejectWhileQuotaBlocked()
    {
        long remaining = QuotaGate.RemainingMs();
        if (remaining <= 0) return;

        Log.Write("Assistente", $"chamada barrada: limite de quota ativo por mais {remaining} ms.");
        throw new InvalidOperationException(QuotaGate.Message(remaining));
    }

    /// <summary>
    /// Espera e permite UMA repetição automática.
    /// </summary>
    /// <remarks>
    /// O 429 do nível gratuito é quase sempre de janela curta (cerca de 20 s) e passa sozinho;
    /// repetir aqui transforma um erro na cara do usuário numa espera. Se depois disso ainda
    /// estourar, o limite é de verdade e o erro sobe.
    /// </remarks>
    private static async Task HandleQuotaAsync(JsonNode? root, string rawBody, int attempt, CancellationToken ct)
    {
        long delay = QuotaGate.ParseRetryDelayMs(root, rawBody);
        QuotaGate.Block(delay);

        bool canRetry = attempt == 0 && delay > 0 && delay <= QuotaGate.MaxAutoRetryMs;
        var apiMessage = root?["error"]?["message"]?.GetValue<string>();
        Log.Write("Assistente", $"limite de quota (429), esperar {delay} ms · " +
                                $"repetir automaticamente={canRetry} · api: {Truncate(apiMessage, LogMessageChars)}");

        if (!canRetry) throw new InvalidOperationException(QuotaGate.Message(delay));

        try
        {
            await Task.Delay((int)delay + 500, ct);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(QuotaGate.Message(delay));
        }
        QuotaGate.Clear();
    }

    private async Task<(int Status, string RawBody, JsonNode? Root)> PostAsync(
        string system, JsonArray contents, CancellationToken ct)
    {
        var body = BuildRequestBody(system, contents);
        var url = $"{BaseUrl}/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        bool hasImage = contents.Any(c => c?["parts"]?.AsArray().Any(p => p?["inlineData"] is not null) == true);
        Log.Write("Assistente", $"POST modelo={_model} · imagem={hasImage} · turnos={contents.Count}");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        int status;
        string rawBody;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            status = (int)response.StatusCode;
            rawBody = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Write("Assistente", "falha de rede ao chamar o Gemini: " + ex);
            throw;
        }

        Log.Write("Assistente", $"resposta HTTP {status} · {rawBody.Length} chars · " +
                                $"corpo={Truncate(rawBody, LogBodyChars)}");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawBody);
        }
        catch (Exception ex)
        {
            root = null;
            Log.Write("Assistente", "corpo não é JSON: " + ex.Message);
        }

        return (status, rawBody, root);
    }

    /// <summary>
    /// Monta o corpo da requisição. O conteúdo é clonado porque um JsonNode só pode ter um pai:
    /// numa repetição após 429 o mesmo array seria anexado a um segundo corpo e a montagem
    /// estouraria.
    /// </summary>
    private JsonObject BuildRequestBody(string system, JsonArray contents) => new()
    {
        ["systemInstruction"] = new JsonObject { ["parts"] = TextParts(system) },
        ["contents"] = contents.DeepClone(),
        ["generationConfig"] = BuildGenerationConfig()
    };

    private JsonObject BuildGenerationConfig()
    {
        var config = new JsonObject
        {
            ["maxOutputTokens"] = MaxOutputTokens,
            ["temperature"] = Temperature
        };

        if (_model.Contains("2.5", StringComparison.OrdinalIgnoreCase))
            config["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };

        return config;
    }

    /// <summary>Junta as partes de texto do primeiro candidato, ou explica por que não houve texto.</summary>
    private static string ReadAnswer(JsonNode? root)
    {
        var text = new StringBuilder();
        if (root?["candidates"] is JsonArray candidates && candidates.Count > 0
            && candidates[0]?["content"]?["parts"] is JsonArray parts)
            foreach (var part in parts)
                text.Append(part?["text"]?.GetValue<string>());

        var answer = text.ToString().Trim();
        if (answer.Length > 0)
        {
            Log.Write("Assistente", $"resposta gerada ({answer.Length} chars).");
            return answer;
        }

        var blockReason = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
        var finishReason = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
        Log.Write("Assistente", $"sem texto · blockReason={blockReason ?? "-"} · finishReason={finishReason ?? "-"}");

        return blockReason is not null
            ? $"(a IA não respondeu — bloqueado: {blockReason})"
            : "(a IA não retornou resposta.)";
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
