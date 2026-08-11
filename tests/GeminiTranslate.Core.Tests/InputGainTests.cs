using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Regressões da regra número um do ganho de entrada: NUNCA CLIPAR.
/// </summary>
/// <remarks>
/// A primeira versão do ganho produziu 0,338% de amostras clipadas concentradas no ataque de
/// cada frase, e o resultado foi o modelo transcrever "teste som" como "É, ainda são" — clipping
/// gera distorção harmônica, que arruína o rastreamento de pitch, que é de onde sai a entonação.
/// Estes testes fixam os dois comportamentos que impedem aquilo: ataque instantâneo e envelope
/// congelado no silêncio.
/// </remarks>
public class InputGainTests
{
    private const int Samples = 1600;

    [Fact]
    public void SinalFracoEAmplificado()
    {
        var gain = new InputGain("teste");
        float saida = 0;

        for (int i = 0; i < 50; i++)
            saida = ApplyAndPeak(gain, 0.10f);

        Assert.True(saida > 0.10f, $"o ganho não subiu: pico de saída ficou em {saida:0.000}");
    }

    [Fact]
    public void NuncaUltrapassaOLimiteSeguro()
    {
        var gain = new InputGain("teste");

        for (int i = 0; i < 50; i++) ApplyAndPeak(gain, 0.10f);

        for (int i = 0; i < 20; i++)
        {
            float peak = ApplyAndPeak(gain, 0.80f);
            Assert.True(peak <= 0.95f, $"pico de {peak:0.000} passou do limite seguro no chunk {i}");
        }
    }

    [Fact]
    public void AtaqueDepoisDeSilencioNaoClipa()
    {
        var gain = new InputGain("teste");

        for (int i = 0; i < 50; i++) ApplyAndPeak(gain, 0.08f);
        for (int i = 0; i < 100; i++) gain.Apply(TestSignals.Silence(Samples));

        float attack = ApplyAndPeak(gain, 0.85f);

        Assert.True(attack <= 0.95f,
            $"o ganho subiu durante o silêncio: o ataque seguinte saiu em {attack:0.000}");
    }

    [Fact]
    public void SinalJaForteNaoEAtenuado()
    {
        var gain = new InputGain("teste");

        float peak = ApplyAndPeak(gain, 0.88f);

        Assert.True(peak >= 0.87f, $"o sinal foi atenuado para {peak:0.000}");
    }

    /// <summary>Aplica o ganho a um chunk do pico dado e devolve o pico resultante.</summary>
    private static float ApplyAndPeak(InputGain gain, float peak)
    {
        var chunk = TestSignals.Constant(Samples, peak);
        gain.Apply(chunk);
        return Pcm.Peak(chunk);
    }
}
