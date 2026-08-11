using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Contagem de fala e de tradução, base da barra de saldo.
/// </summary>
/// <remarks>
/// Conta segundos de FALA, não de relógio: silêncio abaixo do limiar não entra. Foi medir os dois
/// lados em níveis diferentes que já produziu uma razão fala/tradução de 2,65, impossível para
/// qualquer par de idiomas, e travou a barra em "tradução em dia".
/// </remarks>
public class SpeechBalanceTests
{
    private const float Speech = 0.30f;
    private const float BelowThreshold = 0.01f;

    [Fact]
    public void SemFalaFicaInativo()
    {
        var snapshot = new SpeechBalance().Read(0);

        Assert.False(snapshot.Active);
        Assert.Equal(0, snapshot.SpokenMs);
    }

    [Fact]
    public void SilencioNaoContaComoFala()
    {
        var balance = new SpeechBalance();

        for (int i = 0; i < 10; i++)
            balance.Spoke(WireChunk(BelowThreshold));

        Assert.False(balance.Read(0).Active);
    }

    [Fact]
    public void FalaEContadaEmMilissegundosDeAudio()
    {
        var balance = new SpeechBalance();

        for (int i = 0; i < 10; i++)
            balance.Spoke(WireChunk(Speech));

        var snapshot = balance.Read(0);

        Assert.True(snapshot.Active);
        Assert.Equal(1000, snapshot.SpokenMs, 1);
    }

    [Fact]
    public void TraducaoRecebidaContaComoSaida()
    {
        var balance = new SpeechBalance();
        for (int i = 0; i < 10; i++) balance.Spoke(WireChunk(Speech));
        for (int i = 0; i < 5; i++) balance.Heard(DubChunk(Speech));

        var (spokenMs, dubbedMs) = balance.Totals();

        Assert.Equal(1000, spokenMs, 1);
        Assert.Equal(500, dubbedMs, 1);
    }

    [Fact]
    public void OQueEstaNaFilaDeReproducaoAindaNaoSaiu()
    {
        var balance = new SpeechBalance();
        for (int i = 0; i < 10; i++) balance.Spoke(WireChunk(Speech));
        for (int i = 0; i < 10; i++) balance.Heard(DubChunk(Speech));

        var semFila = balance.Read(0);
        var comFila = balance.Read(400);

        Assert.Equal(1000, semFila.PlayedMs, 1);
        Assert.Equal(600, comFila.PlayedMs, 1);
    }

    [Fact]
    public void ADistanciaNaoEDerivadaAqui()
    {
        var balance = new SpeechBalance();
        for (int i = 0; i < 10; i++) balance.Spoke(WireChunk(Speech));

        Assert.True(double.IsNaN(balance.Read(0).GapMs),
            "a distância tem de vir do LatencyProbe, não de subtrair contadores");
    }

    private static byte[] WireChunk(float amplitude) =>
        TestSignals.Constant(TestSignals.ChunkSamples(AudioRates.Wire), amplitude);

    private static byte[] DubChunk(float amplitude) =>
        TestSignals.Constant(TestSignals.ChunkSamples(AudioRates.Dub), amplitude);
}
