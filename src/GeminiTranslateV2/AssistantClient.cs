using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace GeminiTranslateV2;

/// <summary>
/// Assistente que sugere respostas usando a API do Google Gemini (generateContent), que tem
/// camada gratuita no Google AI Studio. Reutiliza a mesma chave usada na tradução — não precisa
/// de outra conta nem cartão. Dada uma pergunta que a outra pessoa fez, sugere em português uma
/// resposta que o usuário pode falar. Modelo padrão: gemini-2.5-flash (gratuito e rápido).
/// </summary>
public sealed class AssistantClient(string apiKey, string model, string persona) : IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model;
    private readonly string _persona = persona?.Trim() ?? "";

    public async Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct)
    {
        var system = new StringBuilder();
        system.Append("Você ajuda o usuário durante uma conversa/reunião ao vivo. ");
        system.Append("A outra pessoa fez uma pergunta ao usuário. Sugira uma resposta objetiva, completa e ");
        system.Append("natural, em português do Brasil, que o usuário possa simplesmente falar. ");
        system.Append("Se a pergunta for técnica, responda de forma correta e direta ao ponto. ");
        system.Append("Termine o raciocínio (não deixe a resposta pela metade). ");
        system.Append("Responda APENAS com a sugestão de resposta, sem preâmbulo, sem aspas e sem explicações sobre o que você está fazendo.");
        if (_persona.Length > 0)
            system.Append("\n\nSobre o usuário (contexto para personalizar a resposta): ").Append(_persona);

        var userMsg = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            userMsg.Append("Contexto recente do que a outra pessoa disse:\n").Append(context.Trim()).Append("\n\n");
        userMsg.Append("Pergunta que preciso responder:\n").Append(question.Trim())
               .Append("\n\nSugira o que eu posso responder.");

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system.ToString() } }
            },
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray { new JsonObject { ["text"] = userMsg.ToString() } }
                }
            },
            ["generationConfig"] = BuildGenerationConfig()
        };

        var url = $"{BaseUrl}/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        Log.Write("Assistente", $"POST modelo={_model} · pergunta='{Trunc(question, 120)}' · contexto={context?.Length ?? 0} chars · persona={_persona.Length} chars");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        string raw;
        try
        {
            resp = await _http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Write("Assistente", "falha de rede ao chamar o Gemini: " + ex);
            throw;
        }

        Log.Write("Assistente", $"resposta HTTP {(int)resp.StatusCode} · {raw.Length} chars · corpo={Trunc(raw, 500)}");

        JsonNode? root;
        try { root = JsonNode.Parse(raw); } catch (Exception ex) { root = null; Log.Write("Assistente", "corpo não é JSON válido: " + ex.Message); }

        if (!resp.IsSuccessStatusCode)
        {
            var apiMsg = root?["error"]?["message"]?.GetValue<string>();
            resp.Dispose();
            Log.Write("Assistente", "erro da API: " + (apiMsg ?? $"HTTP {(int)resp.StatusCode}"));
            throw new InvalidOperationException(apiMsg ?? $"erro HTTP {(int)resp.StatusCode} da API do Gemini.");
        }
        resp.Dispose();

        // candidates[0].content.parts[*].text
        var sb = new StringBuilder();
        if (root?["candidates"] is JsonArray candidates && candidates.Count > 0)
        {
            if (candidates[0]?["content"]?["parts"] is JsonArray parts)
                foreach (var part in parts)
                    sb.Append(part?["text"]?.GetValue<string>());
        }

        var text = sb.ToString().Trim();
        if (text.Length > 0)
        {
            Log.Write("Assistente", $"sugestão gerada ({text.Length} chars).");
            return text;
        }

        // Sem candidato: pode ter sido bloqueado por filtro de segurança.
        var block = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
        var finish = root?["candidates"]?[0]?["finishReason"]?.GetValue<string>();
        Log.Write("Assistente", $"sem texto na resposta · blockReason={block ?? "-"} · finishReason={finish ?? "-"}");
        return block is not null
            ? $"(a IA não sugeriu resposta — bloqueado: {block})"
            : "(a IA não retornou uma sugestão.)";
    }

    private JsonObject BuildGenerationConfig()
    {
        var cfg = new JsonObject
        {
            // Orçamento amplo para a resposta não ser cortada no meio.
            ["maxOutputTokens"] = 1024,
            ["temperature"] = 0.7
        };
        // Nos modelos Gemini 2.5, o "thinking" consome o orçamento de saída e trunca a resposta.
        // Desligamos (thinkingBudget = 0) para o texto sair completo e mais rápido.
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
