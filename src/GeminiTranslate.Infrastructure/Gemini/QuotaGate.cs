using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiTranslate.Infrastructure.Gemini;

/// <summary>
/// Guarda a janela de espera imposta por um HTTP 429 da API do Gemini.
/// </summary>
/// <remarks>
/// O estado é ESTÁTICO de propósito: o limite é da chave e do projeto, não de uma instância de
/// cliente. Recriar o assistente — o que acontece a cada vez que a tradução é reiniciada — não
/// pode zerar o que já se sabe sobre a cota.
/// </remarks>
public static class QuotaGate
{
    /// <summary>
    /// Até quanto tempo vale esperar sozinho antes de devolver erro. Acima disso a espera seria
    /// pior que o erro: a resposta chegaria depois que o assunto da reunião já passou.
    /// </summary>
    public const long MaxAutoRetryMs = 30_000;

    /// <summary>Janela típica do nível gratuito, usada quando o servidor não informa a dele.</summary>
    private const long DefaultCooldownMs = 20_000;

    private static readonly Regex RetryInMessage =
        new(@"retry in ([0-9]+(?:\.[0-9]+)?)s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static long _blockedUntil;

    /// <summary>Quanto falta da espera atual, ou zero quando a cota já está liberada.</summary>
    public static long RemainingMs()
    {
        long left = Interlocked.Read(ref _blockedUntil) - Environment.TickCount64;
        return left > 0 ? left : 0;
    }

    /// <summary>
    /// Registra uma nova espera. Só ESTENDE, nunca encurta: dois 429 em sequência não podem abrir
    /// a porta antes da hora.
    /// </summary>
    public static void Block(long delayMs)
    {
        if (delayMs <= 0) delayMs = DefaultCooldownMs;
        long until = Environment.TickCount64 + delayMs;

        long current;
        while ((current = Interlocked.Read(ref _blockedUntil)) < until)
            if (Interlocked.CompareExchange(ref _blockedUntil, until, current) == current) break;
    }

    /// <summary>Libera a cota depois de uma espera cumprida com sucesso.</summary>
    public static void Clear() => Interlocked.Exchange(ref _blockedUntil, 0);

    /// <summary>Mensagem exibida ao usuário durante a espera.</summary>
    public static string Message(long delayMs)
    {
        int seconds = (int)Math.Ceiling(Math.Max(delayMs, 1000) / 1000.0);
        return $"Limite de uso gratuito do Gemini atingido. Aguarde {seconds} s e tente de novo " +
               "(reclicar agora só renova a espera).";
    }

    /// <summary>
    /// Quanto o servidor pediu para esperar, em milissegundos, ou zero quando não informou.
    /// </summary>
    /// <remarks>
    /// Vem em <c>error.details[]</c> como RetryInfo (<c>retryDelay: "19s"</c>); quando esse campo
    /// não vem, a mesma informação aparece em texto no fim da mensagem ("Please retry in
    /// 19.835657224s."). Lê os dois.
    /// </remarks>
    public static long ParseRetryDelayMs(JsonNode? root, string rawBody) =>
        FromDetails(root) is { } fromDetails ? fromDetails : FromText(rawBody);

    private static long? FromDetails(JsonNode? root)
    {
        try
        {
            if (root?["error"]?["details"] is not JsonArray details) return null;

            foreach (var detail in details)
            {
                var value = detail?["retryDelay"]?.GetValue<string>();
                if (string.IsNullOrEmpty(value)) continue;
                if (TryParseSeconds(value.TrimEnd('s', 'S'), out long ms)) return ms;
            }
        }
        catch
        {
        }
        return null;
    }

    private static long FromText(string rawBody)
    {
        var match = RetryInMessage.Match(rawBody);
        return match.Success && TryParseSeconds(match.Groups[1].Value, out long ms) ? ms : 0;
    }

    private static bool TryParseSeconds(string text, out long milliseconds)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds);
        milliseconds = parsed ? (long)(seconds * 1000) : 0;
        return parsed;
    }
}
