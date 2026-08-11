using GeminiTranslate.Core.Signal;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Fixa a curva de recuperação de fila.
/// </summary>
/// <remarks>
/// O piso não é zero de propósito: a fila oscila naturalmente entre 90 e 330 ms, e acelerar
/// nessa faixa trocaria atraso por engasgo. O teto é o ponto em que o WSOLA deixa de ser
/// inaudível em fala.
/// </remarks>
public class CatchUpPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(330)]
    [InlineData(350)]
    public void FilaNormalTocaEmVelocidadeExata(double queueMs)
    {
        Assert.Equal(1.0, CatchUpPolicy.SpeedFor(queueMs));
    }

    [Theory]
    [InlineData(900)]
    [InlineData(2000)]
    [InlineData(60000)]
    public void FilaGrandeAceleraNoMaximo(double queueMs)
    {
        Assert.Equal(1.12, CatchUpPolicy.SpeedFor(queueMs), 6);
    }

    [Fact]
    public void EntreOPisoEOTetoARampaECrescente()
    {
        double anterior = CatchUpPolicy.SpeedFor(350);

        for (double queue = 400; queue <= 900; queue += 50)
        {
            double atual = CatchUpPolicy.SpeedFor(queue);
            Assert.True(atual > anterior, $"a rampa não cresceu em {queue} ms");
            anterior = atual;
        }
    }

    [Fact]
    public void NuncaPassaDoTetoInaudivel()
    {
        for (double queue = 0; queue <= 10000; queue += 25)
            Assert.InRange(CatchUpPolicy.SpeedFor(queue), 1.0, 1.12);
    }
}
