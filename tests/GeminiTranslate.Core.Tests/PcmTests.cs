using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Garante as primitivas de PCM, das quais dependem todos os limiares de fala do sistema.
/// </summary>
/// <remarks>
/// Estas contas já estiveram copiadas em cinco classes. Uma divergência entre elas desalinharia
/// silenciosamente o que <c>LatencyProbe</c>, <c>SpeechBalance</c> e <c>InputGain</c> consideram
/// fala, que foi como uma sessão fechou com razão fala/tradução de 2,65.
/// </remarks>
public class PcmTests
{
    [Fact]
    public void SilencioTemRmsZero()
    {
        Assert.Equal(0f, Pcm.Rms(TestSignals.Silence(1600)));
    }

    [Fact]
    public void RmsDeOndaQuadradaIgualaAmplitude()
    {
        var pcm = TestSignals.Constant(1600, 0.5f);
        Assert.Equal(0.5f, Pcm.Rms(pcm), 3);
    }

    [Fact]
    public void PicoEncontraMaiorValorAbsoluto()
    {
        var pcm = TestSignals.Sine(1600, 0.75f, 440, 16000);
        Assert.InRange(Pcm.Peak(pcm), 0.70f, 0.76f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(0.999f)]
    public void AmostraSobreviveAoIdaEVolta(float value)
    {
        var pcm = new byte[Pcm.BytesPerSample];
        Pcm.WriteSample(pcm, 0, value);

        Assert.Equal(value, Pcm.SampleAt(pcm, 0), 3);
    }

    [Fact]
    public void EscritaLimitaEmVezDeEstourar()
    {
        var pcm = new byte[Pcm.BytesPerSample];
        Pcm.WriteSample(pcm, 0, 2.5f);

        Assert.InRange(Pcm.SampleAt(pcm, 0), 0.99f, 1.0f);
    }

    [Fact]
    public void DuracaoUsaTaxaInformada()
    {
        var chunk = TestSignals.Silence(TestSignals.ChunkSamples(AudioRates.Wire));

        Assert.Equal(100, Pcm.DurationMs(chunk, AudioRates.Wire), 3);
    }
}
