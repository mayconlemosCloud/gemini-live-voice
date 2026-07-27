using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GeminiTranslateV2;

/// <summary>
/// Assistente que usa a API do Google Gemini (generateContent), com camada gratuita, para
/// sugerir respostas e analisar prints da tela — sempre com o contexto da conversa. Reutiliza a
/// mesma chave usada na tradução. Modelo padrão: gemini-2.5-flash (thinking desligado p/ não
/// truncar). Cobre três modos: sugerir resposta a uma pergunta, sugerir a partir da conversa
/// inteira, e analisar uma imagem (visão).
/// </summary>
public sealed class AssistantClient(string apiKey, string model, string persona) : IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model;
    private readonly string _persona = persona?.Trim() ?? "";

    private string PersonaLine => _persona.Length > 0
        ? "\n\nSobre o usuário (para personalizar): " + _persona
        : "";

    /// <summary>Sugere resposta a UMA pergunta específica (ex.: trecho clicado/selecionado).</summary>
    public Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct)
    {
        var system =
            "Você ajuda o usuário durante uma conversa/reunião ao vivo. A outra pessoa fez uma " +
            "pergunta ao usuário. Sugira uma resposta objetiva, completa e natural, em português do " +
            "Brasil, que o usuário possa simplesmente falar. Se for técnica, responda de forma correta " +
            "e direta ao ponto. Termine o raciocínio (não deixe pela metade). Responda APENAS com a " +
            "sugestão de resposta, sem preâmbulo, sem aspas." + PersonaLine;

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            user.Append("Contexto recente da conversa:\n").Append(context.Trim()).Append("\n\n");
        user.Append("Pergunta que preciso responder:\n").Append(question.Trim())
            .Append("\n\nSugira o que eu posso responder.");

        return CallAsync(system, TextParts(user.ToString()), ct);
    }

    /// <summary>Analisa a conversa INTEIRA e sugere possíveis respostas para o momento atual.</summary>
    public Task<string> SuggestFromConversationAsync(string context, CancellationToken ct)
    {
        var system =
            "Você ajuda o usuário durante uma conversa/reunião ao vivo. A seguir está a transcrição " +
            "recente (rótulos: 'Eles' = a outra pessoa, 'Você' = o usuário). Com base em TODO o " +
            "contexto, sugira em português do Brasil o que o usuário pode responder agora. Dê 2 a 3 " +
            "opções curtas de resposta, numeradas, prontas para falar. Seja objetivo e completo." + PersonaLine;

        var user = "Transcrição da conversa:\n" + (context?.Trim() ?? "(vazia)") +
                   "\n\nSugira o que eu posso responder agora.";

        return CallAsync(system, TextParts(user), ct);
    }

    /// <summary>Analisa uma imagem (print) no contexto da conversa. Visão.</summary>
    public Task<string> AnalyzeImageAsync(byte[] png, string context, CancellationToken ct)
    {
        var system =
            "Você ajuda o usuário durante uma conversa/reunião ao vivo. Ele enviou um print da tela. " +
            "Analise a imagem e, considerando o contexto da conversa, ajude de forma prática em " +
            "português do Brasil: descreva o que é relevante na imagem e sugira o que o usuário pode " +
            "dizer ou fazer. Seja objetivo e completo." + PersonaLine;

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

        return CallAsync(system, parts, ct);
    }

    // ---- núcleo ----

    private static JsonArray TextParts(string text) =>
        new() { new JsonObject { ["text"] = text } };

    private async Task<string> CallAsync(string system, JsonArray userParts, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = TextParts(system) },
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = userParts }
            },
            ["generationConfig"] = BuildGenerationConfig()
        };

        var url = $"{BaseUrl}/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        bool hasImage = userParts.Any(p => p?["inlineData"] is not null);
        Log.Write("Assistente", $"POST modelo={_model} · imagem={hasImage} · partes={userParts.Count}");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        string raw;
        int status;
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            raw = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Write("Assistente", "falha de rede ao chamar o Gemini: " + ex);
            throw;
        }

        Log.Write("Assistente", $"resposta HTTP {status} · {raw.Length} chars · corpo={Trunc(raw, 500)}");

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (Exception ex) { root = null; Log.Write("Assistente", "corpo não é JSON: " + ex.Message); }

        if (status is < 200 or >= 300)
        {
            var apiMsg = root?["error"]?["message"]?.GetValue<string>();
            Log.Write("Assistente", "erro da API: " + (apiMsg ?? $"HTTP {status}"));
            throw new InvalidOperationException(apiMsg ?? $"erro HTTP {status} da API do Gemini.");
        }

        var sb = new StringBuilder();
        if (root?["candidates"] is JsonArray candidates && candidates.Count > 0
            && candidates[0]?["content"]?["parts"] is JsonArray parts)
        {
            foreach (var part in parts)
                sb.Append(part?["text"]?.GetValue<string>());
        }

        var text = sb.ToString().Trim();
        if (text.Length > 0)
        {
            Log.Write("Assistente", $"resposta gerada ({text.Length} chars).");
            return text;
        }

        var block = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
        var finish = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
        Log.Write("Assistente", $"sem texto · blockReason={block ?? "-"} · finishReason={finish ?? "-"}");
        return block is not null
            ? $"(a IA não respondeu — bloqueado: {block})"
            : "(a IA não retornou resposta.)";
    }

    private JsonObject BuildGenerationConfig()
    {
        var cfg = new JsonObject
        {
            ["maxOutputTokens"] = 1024,
            ["temperature"] = 0.7
        };
        // Nos modelos 2.5, o "thinking" consome o orçamento de saída e trunca a resposta.
        if (_model.Contains("2.5", StringComparison.OrdinalIgnoreCase))
            cfg["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
        return cfg;
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    public void Dispose() => _http.Dispose();
}
