using System.Net.Http;
using System.Text.Json;
using GeminiLiveTranslate.Logging;

namespace GeminiLiveTranslate.Billing;

/// <summary>
/// Turns accumulated token counts into an estimated cost in USD and BRL.
/// The Gemini API exposes no real-time "total spent" endpoint, so this is an estimate
/// derived from the per-turn usageMetadata token counts and editable prices.
/// </summary>
public sealed class CostMeter
{
    public double InputUsdPerMillion { get; set; } = 3.0;
    public double OutputUsdPerMillion { get; set; } = 12.0;
    public double UsdToBrl { get; set; } = 5.20;

    public double CostUsd(long inputTokens, long outputTokens) =>
        inputTokens / 1_000_000.0 * InputUsdPerMillion +
        outputTokens / 1_000_000.0 * OutputUsdPerMillion;

    public double CostBrl(long inputTokens, long outputTokens) => CostUsd(inputTokens, outputTokens) * UsdToBrl;

    /// <summary>Fetches the live USD→BRL rate from AwesomeAPI (no key required). Returns null on failure.</summary>
    public static async Task<double?> FetchUsdToBrlAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync("https://economia.awesomeapi.com.br/json/last/USD-BRL");
            using var doc = JsonDocument.Parse(json);
            var bid = doc.RootElement.GetProperty("USDBRL").GetProperty("bid").GetString();
            if (double.TryParse(bid, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var rate) && rate > 0)
            {
                Log.Info("Cost", $"Câmbio USD→BRL atualizado: {rate:F4}");
                return rate;
            }
        }
        catch (Exception ex) { Log.Warn("Cost", "Falha ao buscar câmbio: " + ex.Message); }
        return null;
    }
}
