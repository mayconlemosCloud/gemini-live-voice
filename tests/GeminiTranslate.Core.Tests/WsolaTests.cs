using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Verifica as duas propriedades que justificam o WSOLA existir no lugar da interpolação linear
/// que havia antes: ele muda o RITMO sem mexer no PITCH nem no nível.
/// </summary>
/// <remarks>
/// A versão anterior subia o pitch na mesma proporção da velocidade, destruindo a entonação que
/// o modelo tinha acabado de copiar da voz original.
/// </remarks>
public class WsolaTests
{
    private const int Rate = AudioRates.Dub;
    private const double Frequency = 220.0;

    [Fact]
    public void EmVelocidadeNormalPreservaONivel()
    {
        var source = new SineReader(Frequency, Rate, amplitude: 0.5f);
        var wsola = new Wsola(source, () => 1.0);

        float rms = RmsOf(Read(wsola, 48000));

        Assert.InRange(rms, 0.30f, 0.40f);
    }

    [Fact]
    public void EmVelocidadeNormalPreservaAFrequencia()
    {
        var source = new SineReader(Frequency, Rate, amplitude: 0.5f);
        var wsola = new Wsola(source, () => 1.0);

        double measured = DominantFrequency(Read(wsola, 48000), Rate);

        Assert.InRange(measured, Frequency * 0.97, Frequency * 1.03);
    }

    [Fact]
    public void AceleradoPreservaAFrequencia()
    {
        var source = new SineReader(Frequency, Rate, amplitude: 0.5f);
        var wsola = new Wsola(source, () => 1.12);

        var output = Read(wsola, 200000);
        double measured = DominantFrequency(output.AsSpan(50000).ToArray(), Rate);

        Assert.InRange(measured, Frequency * 0.97, Frequency * 1.03);
    }

    [Fact]
    public void AceleradoConsomeMaisEntradaDoQueProduzSaida()
    {
        var source = new SineReader(Frequency, Rate, amplitude: 0.5f);
        var wsola = new Wsola(source, () => 1.12);

        const int produced = 200000;
        Read(wsola, produced);

        double ratio = source.SamplesRead / (double)produced;
        Assert.InRange(ratio, 1.09, 1.15);
    }

    [Fact]
    public void VelocidadeSobeSuavementeEmVezDeSaltar()
    {
        var source = new SineReader(Frequency, Rate, amplitude: 0.5f);
        var wsola = new Wsola(source, () => 1.12);

        Read(wsola, 512);

        Assert.InRange(wsola.Speed, 1.0, 1.05);
    }

    private static float[] Read(Wsola wsola, int count)
    {
        var buffer = new float[count];
        wsola.Read(buffer, 0, count);
        return buffer;
    }

    private static float RmsOf(float[] samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Frequência estimada pela contagem de cruzamentos por zero ascendentes.</summary>
    private static double DominantFrequency(float[] samples, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
            if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;

        return crossings * (double)sampleRate / samples.Length;
    }

    /// <summary>Senoide infinita que conta quantas amostras foram consumidas.</summary>
    private sealed class SineReader(double frequency, int sampleRate, float amplitude) : ISampleReader
    {
        private long _position;

        public long SamplesRead => _position;

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] =
                    (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (_position + i) / sampleRate));

            _position += count;
            return count;
        }
    }
}
