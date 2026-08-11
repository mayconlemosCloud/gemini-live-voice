using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Session;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Fixa as recusas de configuração, com destaque para as duas formas de realimentação de áudio.
/// </summary>
/// <remarks>
/// Escutar o próprio fone recaptura a tradução em loop infinito; escutar o cabo do microfone
/// virtual devolve a própria voz traduzida como entrada. Nenhuma das duas dá erro de dispositivo:
/// elas simplesmente arruínam a chamada, então precisam ser barradas antes de começar.
/// </remarks>
public class SessionValidatorTests
{
    private const string Fone = "fone";
    private const string Mic = "mic";
    private const string CaboVirtual = "cabo-virtual";
    private const string Outro = "outro-cabo";

    [Fact]
    public void ConfiguracaoValidaPassa()
    {
        SessionValidator.Validate(Valid(), new DeviceSourceChoice(Outro, "Outro"));
    }

    [Fact]
    public void ExigeChaveDeApi()
    {
        var settings = Valid();
        settings.ApiKey = "   ";

        var error = Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(settings, new DeviceSourceChoice(Outro, "Outro")));
        Assert.Contains("API key", error.Message);
    }

    [Fact]
    public void ExigeOrigemEscolhida()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(Valid(), null));
        Assert.Contains("escolha o que escutar", error.Message);
    }

    [Fact]
    public void ExigeOsTresDispositivos()
    {
        var settings = Valid();
        settings.VirtualMicDeviceId = null;

        Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(settings, new DeviceSourceChoice(Outro, "Outro")));
    }

    [Fact]
    public void RecusaMicrofoneVirtualIgualAoFone()
    {
        var settings = Valid();
        settings.VirtualMicDeviceId = Fone;

        var error = Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(settings, new DeviceSourceChoice(Outro, "Outro")));
        Assert.Contains("dispositivo separado", error.Message);
    }

    [Fact]
    public void RecusaEscutarOProprioFone()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(Valid(), new DeviceSourceChoice(Fone, "Fone")));

        Assert.Contains("loop", error.Message);
    }

    [Fact]
    public void RecusaEscutarOCaboDoMicrofoneVirtual()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SessionValidator.Validate(Valid(), new DeviceSourceChoice(CaboVirtual, "Cabo")));

        Assert.Contains("própria voz traduzida", error.Message);
    }

    [Fact]
    public void ProcessoNaoSofreAsRestricoesDeCabo()
    {
        SessionValidator.Validate(Valid(), new ProcessSourceChoice("Teams"));
    }

    private static Settings Valid() => new()
    {
        ApiKey = "chave",
        HeadphonesDeviceId = Fone,
        MicDeviceId = Mic,
        VirtualMicDeviceId = CaboVirtual
    };
}
