using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Regressões do estimador contínuo de atraso, exercitado com envelopes sintéticos.
/// </summary>
/// <remarks>
/// Este estimador já reportou 6,75 s para um atraso real de 3,5 s: com frases rítmicas, a
/// correlação tem picos quase iguais em lag, lag+período, lag+2×período, e o máximo global cai
/// num harmônico com facilidade. Poder reproduzir isso em memória, em milissegundos, é
/// exatamente o que a separação entre núcleo e adaptadores comprou.
/// </remarks>
public class EnvelopeLagEstimatorTests
{
    private const int StepMs = 100;
    private const int DurationMs = 40_000;
    private const int SpanBins = StepMs / EnvelopeLagEstimator.BinMs;
    private const float SpeechLevel = 0.30f;

    [Fact]
    public void EncontraOAtrasoDeUmaFalaIrregular()
    {
        const double lagMs = 3500;
        var speech = IrregularSpeech(seed: 7);

        var estimator = Feed(speech, lagMs);

        Assert.InRange(estimator.EstimateMs(DurationMs, 0), lagMs - 500, lagMs + 500);
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(2500)]
    [InlineData(5000)]
    public void SegueOAtrasoEmVariasFaixas(double lagMs)
    {
        var speech = IrregularSpeech(seed: 21);

        var estimator = Feed(speech, lagMs);

        Assert.InRange(estimator.EstimateMs(DurationMs, 0), lagMs - 600, lagMs + 600);
    }

    [Fact]
    public void SomaAFilaDeReproducaoAoAtrasoMedido()
    {
        const double lagMs = 3000;
        const double playoutMs = 250;
        var speech = IrregularSpeech(seed: 3);

        var estimator = Feed(speech, lagMs);

        double semFila = estimator.EstimateMs(DurationMs, 0);
        double comFila = estimator.EstimateMs(DurationMs, playoutMs);

        Assert.Equal(semFila + playoutMs, comFila, 6);
    }

    [Fact]
    public void SemNadaMedidoNaoChuta()
    {
        var estimator = new EnvelopeLagEstimator();

        Assert.True(double.IsNaN(estimator.EstimateMs(0, 0)));
    }

    [Fact]
    public void SilencioDosDoisLadosNaoProduzMedida()
    {
        var estimator = new EnvelopeLagEstimator();

        for (int t = 0; t < DurationMs; t += StepMs)
        {
            estimator.MarkSent(t, SpanBins, 0f);
            estimator.MarkDub(t, SpanBins, 0f);
        }

        Assert.True(double.IsNaN(estimator.EstimateMs(DurationMs, 0)));
    }

    /// <summary>Alimenta os dois envelopes, com o dub atrasado de <paramref name="lagMs"/>.</summary>
    private static EnvelopeLagEstimator Feed(Func<double, bool> speaking, double lagMs)
    {
        var estimator = new EnvelopeLagEstimator();

        for (int t = 0; t < DurationMs; t += StepMs)
        {
            estimator.MarkSent(t, SpanBins, speaking(t) ? SpeechLevel : 0f);
            estimator.MarkDub(t, SpanBins, speaking(t - lagMs) ? SpeechLevel : 0f);
        }
        return estimator;
    }

    /// <summary>
    /// Fala com frases de duração irregular, como numa conversa real. A irregularidade importa:
    /// com frases perfeitamente periódicas a correlação é ambígua por construção.
    /// </summary>
    private static Func<double, bool> IrregularSpeech(int seed)
    {
        var random = new Random(seed);
        var segments = new List<(double Start, double End)>();

        double at = 0;
        while (at < DurationMs * 2)
        {
            double speech = 800 + random.NextDouble() * 2500;
            double pause = 500 + random.NextDouble() * 1500;
            segments.Add((at, at + speech));
            at += speech + pause;
        }

        return t => t >= 0 && segments.Any(s => t >= s.Start && t < s.End);
    }
}
