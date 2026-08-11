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

    /// <summary>
    /// Sugere resposta a UMA pergunta específica (trecho clicado, ou a última pergunta detectada).
    /// A pergunta sozinha costuma ser ambígua ("e isso aí?", "quanto tempo leva?") — o modelo recebe
    /// a transcrição dos dois lados para entender do que se trata, mas responde SÓ essa pergunta.
    /// </summary>
    public Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct)
    {
        var system =
            "Você ajuda o usuário durante uma conversa/reunião ao vivo. A outra pessoa fez uma " +
            "pergunta ao usuário.\n\n" +
            "A transcrição é automática e vem de tradução ao vivo: pode ter erros, cortes e frases " +
            "soltas. Rótulos: 'Eles' = a outra pessoa, 'Você' = o usuário.\n\n" +
            "Regras:\n" +
            "1. Use TODA a transcrição para entender do que a pergunta trata — resolva pronomes e " +
            "referências implícitas ('isso', 'ele', 'esse prazo', 'lá') pelo assunto da conversa.\n" +
            "2. Responda APENAS à pergunta indicada abaixo como a pergunta atual. Não responda " +
            "perguntas anteriores, não resuma a conversa, não liste opções.\n" +
            "3. Se, mesmo com o contexto, a pergunta continuar ambígua, adote a interpretação mais " +
            "provável e responda a ela (sem avisar que assumiu algo).\n" +
            "4. A resposta deve ser objetiva, completa e natural, em português do Brasil, pronta para " +
            "o usuário simplesmente falar. Se for técnica, seja correto e direto ao ponto. Termine o " +
            "raciocínio (não deixe pela metade).\n" +
            "5. Responda APENAS com a sugestão de resposta: sem preâmbulo, sem aspas, sem explicar o " +
            "contexto." + PersonaLine;

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context))
            user.Append("Transcrição da conversa até agora (para você entender o assunto):\n")
                .Append(context.Trim()).Append("\n\n");
        user.Append("Pergunta atual, que é a única que devo responder:\n").Append(question.Trim())
            .Append("\n\nSugira o que eu posso responder a essa pergunta.");

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

    /// <summary>
    /// Uma fala do chat do assistente. <paramref name="FromUser"/> separa o que o usuário digitou
    /// do que a IA respondeu — é o que vira o campo "role" que a API exige.
    /// </summary>
    public sealed record ChatTurn(bool FromUser, string Text);

    /// <summary>
    /// Pergunta livre digitada pelo usuário, com o histórico do próprio chat. Diferente dos outros
    /// modos, aqui o usuário conduz: pode pedir esclarecimento, mudar de assunto ou continuar de
    /// onde parou, então o histórico vai inteiro e o contexto da reunião entra só como pano de
    /// fundo. <paramref name="history"/> deve terminar na pergunta atual.
    /// </summary>
    public Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, string context, CancellationToken ct)
    {
        var system =
            "Você é o copiloto do usuário durante uma conversa/reunião ao vivo, respondendo no chat " +
            "lateral do app. Responda em português do Brasil, de forma objetiva, correta e completa.\n\n" +
            "O usuário fala com VOCÊ aqui — não é a outra pessoa da reunião falando. Responda o que " +
            "ele pedir: pode ser tirar uma dúvida, pedir uma sugestão de fala, explicar algo dito na " +
            "reunião ou qualquer outro assunto.\n\n" +
            "A transcrição abaixo é o que está sendo dito na reunião ('Eles' = a outra pessoa, " +
            "'Você' = o usuário). Ela é contexto de apoio, automática e sujeita a erros: use quando " +
            "ajudar a entender a pergunta, e ignore quando a pergunta não tiver relação com ela.\n\n" +
            "Transcrição da reunião até agora:\n" +
            (string.IsNullOrWhiteSpace(context) ? "(ainda vazia)" : context.Trim()) +
            PersonaLine;

        var contents = new JsonArray();
        foreach (var turn in history)
        {
            contents.Add(new JsonObject
            {
                ["role"] = turn.FromUser ? "user" : "model",
                ["parts"] = TextParts(turn.Text)
            });
        }

        return CallContentsAsync(system, contents, ct);
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

    /// <summary>Um único turno de usuário — a forma dos modos de sugestão e de imagem.</summary>
    private Task<string> CallAsync(string system, JsonArray userParts, CancellationToken ct) =>
        CallContentsAsync(system, new JsonArray
        {
            new JsonObject { ["role"] = "user", ["parts"] = userParts }
        }, ct);

    private async Task<string> CallContentsAsync(string system, JsonArray contents, CancellationToken ct)
    {
        // Uma chamada mandada DENTRO da janela de espera de um 429 anterior não tem chance de dar
        // certo: ela volta 429 e ainda mantém o balde de quota vazio por mais tempo. Nos logs de
        // 11/08 esse foi o padrão dominante — um 429 seguido de 4–5 recliques em 12 s, todos 429.
        // Então aqui a chamada nem sai: falha na hora, dizendo quanto falta.
        long wait = RemainingCooldownMs();
        if (wait > 0)
        {
            Log.Write("Assistente", $"chamada barrada: limite de quota ativo por mais {wait} ms.");
            throw new InvalidOperationException(CooldownMessage(wait));
        }

        // Uma repetição automática, e só uma: o 429 do nível gratuito é quase sempre de janela
        // curta (~20 s) e passa sozinho. Repetir aqui transforma um erro na cara do usuário numa
        // espera. Se depois disso ainda estourar, o limite é de verdade e o erro sobe.
        for (var attempt = 0; ; attempt++)
        {
            var (status, raw, root) = await PostAsync(system, contents, ct);

            if (status == 429)
            {
                long delay = ParseRetryDelayMs(root, raw);
                SetCooldown(delay);

                bool canRetry = attempt == 0 && delay > 0 && delay <= MaxAutoRetryMs;
                var apiMsg = root?["error"]?["message"]?.GetValue<string>();
                Log.Write("Assistente", $"limite de quota (429), esperar {delay} ms · " +
                                        $"repetir automaticamente={canRetry} · api: {Trunc(apiMsg, 200)}");

                if (!canRetry) throw new InvalidOperationException(CooldownMessage(delay));

                try { await Task.Delay((int)delay + 500, ct); }
                catch (OperationCanceledException) { throw new InvalidOperationException(CooldownMessage(delay)); }
                ClearCooldown();
                continue;
            }

            if (status is < 200 or >= 300)
            {
                var apiMsg = root?["error"]?["message"]?.GetValue<string>();
                Log.Write("Assistente", "erro da API: " + (apiMsg ?? $"HTTP {status}"));
                throw new InvalidOperationException(apiMsg ?? $"erro HTTP {status} da API do Gemini.");
            }

            return ReadAnswer(root);
        }
    }

    private async Task<(int Status, string Raw, JsonNode? Root)> PostAsync(
        string system, JsonArray contents, CancellationToken ct)
    {
        // DeepClone porque um JsonNode só pode ter um pai: numa repetição após 429 este mesmo
        // 'contents' seria anexado a um segundo body e a montagem estouraria InvalidOperationException.
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = TextParts(system) },
            ["contents"] = contents.DeepClone(),
            ["generationConfig"] = BuildGenerationConfig()
        };

        var url = $"{BaseUrl}/{Uri.EscapeDataString(_model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        bool hasImage = contents.Any(c => c?["parts"]?.AsArray().Any(p => p?["inlineData"] is not null) == true);
        Log.Write("Assistente", $"POST modelo={_model} · imagem={hasImage} · turnos={contents.Count}");

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

        return (status, raw, root);
    }

    private static string ReadAnswer(JsonNode? root)
    {
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

    // ---- controle de quota (429) ----

    /// <summary>
    /// Até quanto tempo vale esperar sozinho antes de devolver erro. Acima disso a espera seria
    /// pior que o erro — a resposta chegaria depois que o assunto da reunião já passou.
    /// </summary>
    private const long MaxAutoRetryMs = 30_000;

    /// <summary>
    /// Quando a quota volta a estar disponível (<see cref="Environment.TickCount64"/>). Estático de
    /// propósito: o limite é da chave/projeto, não desta instância, então recriar o AssistantClient
    /// (o que acontece a cada vez que a tradução é reiniciada) não pode zerar o que já se sabe.
    /// </summary>
    private static long _quotaBlockedUntil;

    private static long RemainingCooldownMs()
    {
        long left = Interlocked.Read(ref _quotaBlockedUntil) - Environment.TickCount64;
        return left > 0 ? left : 0;
    }

    private static void SetCooldown(long delayMs)
    {
        // Sem retryDelay no corpo, assume a janela típica do nível gratuito.
        if (delayMs <= 0) delayMs = 20_000;
        long until = Environment.TickCount64 + delayMs;
        // Só estende, nunca encurta: dois 429 em sequência não podem abrir a porta antes da hora.
        long current;
        while ((current = Interlocked.Read(ref _quotaBlockedUntil)) < until)
            if (Interlocked.CompareExchange(ref _quotaBlockedUntil, until, current) == current) break;
    }

    private static void ClearCooldown() => Interlocked.Exchange(ref _quotaBlockedUntil, 0);

    private static string CooldownMessage(long delayMs)
    {
        int s = (int)Math.Ceiling(Math.Max(delayMs, 1000) / 1000.0);
        return $"Limite de uso gratuito do Gemini atingido. Aguarde {s} s e tente de novo " +
               "(reclicar agora só renova a espera).";
    }

    private static readonly System.Text.RegularExpressions.Regex RetryInMessage =
        new(@"retry in ([0-9]+(?:\.[0-9]+)?)s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Quanto o servidor pediu para esperar. Vem em <c>error.details[]</c> como RetryInfo
    /// (<c>retryDelay: "19s"</c>); quando esse campo não vem, a mesma informação aparece em texto
    /// no fim da mensagem ("Please retry in 19.835657224s."). Lê os dois.
    /// </summary>
    private static long ParseRetryDelayMs(JsonNode? root, string raw)
    {
        try
        {
            if (root?["error"]?["details"] is JsonArray details)
            {
                foreach (var d in details)
                {
                    var value = d?["retryDelay"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(value)) continue;
                    if (double.TryParse(value!.TrimEnd('s', 'S'),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var secs))
                        return (long)(secs * 1000);
                }
            }
        }
        catch { /* corpo fora do formato esperado; cai no texto abaixo */ }

        var m = RetryInMessage.Match(raw);
        if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var fromText))
            return (long)(fromText * 1000);

        return 0;
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    public void Dispose() => _http.Dispose();
}
