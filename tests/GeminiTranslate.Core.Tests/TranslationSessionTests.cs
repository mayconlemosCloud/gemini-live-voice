using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Session;
using Xunit;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Exercita a montagem completa de uma sessão contra uma plataforma de mentira.
/// </summary>
/// <remarks>
/// Esta orquestração já morou dentro de um manipulador de clique, o que a tornava impossível de
/// verificar sem uma interface e sem hardware — e fácil de vazar recursos num caminho de erro.
/// </remarks>
public class TranslationSessionTests
{
    [Fact]
    public async Task IniciaAsDuasDirecoes()
    {
        var platform = new FakePlatform();

        using var session = await StartAsync(platform);

        Assert.True(platform.Entrada!.Started);
        Assert.True(platform.Microphone!.Started);
        Assert.Equal(2, platform.Streams.Count);
        Assert.All(platform.Streams, s => Assert.True(s.Connected));
    }

    [Fact]
    public async Task UsaORotuloDaOrigemEscolhida()
    {
        var platform = new FakePlatform();

        using var session = await StartAsync(platform, new DeviceSourceChoice("cabo", "CABLE Input"));

        Assert.Equal("CABLE Input", session.IncomingLabel);
    }

    [Fact]
    public async Task CriaOAssistenteQuandoLigadoEComChave()
    {
        var settings = Valid();
        settings.AssistantEnabled = true;

        using var session = await StartAsync(new FakePlatform(), settings: settings);

        Assert.NotNull(session.Assistant);
    }

    [Fact]
    public async Task NaoCriaOAssistenteQuandoDesligado()
    {
        var settings = Valid();
        settings.AssistantEnabled = false;

        using var session = await StartAsync(new FakePlatform(), settings: settings);

        Assert.Null(session.Assistant);
    }

    [Fact]
    public async Task SemChaveNemChegaAMontarASessao()
    {
        var settings = Valid();
        settings.AssistantEnabled = true;
        settings.ApiKey = "   ";

        var platform = new FakePlatform();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(platform, settings: settings));
    }

    [Fact]
    public async Task AssumeOsPadroesApenasQuandoPedido()
    {
        var comCabos = Valid();
        comCabos.MakeCablesDefault = true;
        var platformCom = new FakePlatform();
        using (await StartAsync(platformCom, settings: comCabos)) { }

        var semCabos = Valid();
        semCabos.MakeCablesDefault = false;
        var platformSem = new FakePlatform();
        using (await StartAsync(platformSem, settings: semCabos)) { }

        Assert.True(platformCom.DefaultDevicesApplied);
        Assert.False(platformSem.DefaultDevicesApplied);
    }

    [Fact]
    public async Task ProcessoNaoTrocaASaidaPadrao()
    {
        var platform = new FakePlatform();

        using var session = await StartAsync(platform, new ProcessSourceChoice("Teams"));

        Assert.Null(platform.DefaultDevicesEntradaId);
    }

    [Fact]
    public async Task DescartarLiberaTudoQueFoiCriado()
    {
        var platform = new FakePlatform();
        var session = await StartAsync(platform);

        session.Dispose();

        Assert.True(platform.AllDisposed, "sobrou recurso não descartado depois de parar a sessão");
    }

    [Fact]
    public async Task DescartarDuasVezesEInofensivo()
    {
        var platform = new FakePlatform();
        var session = await StartAsync(platform);

        session.Dispose();
        session.Dispose();

        Assert.True(platform.AllDisposed);
    }

    [Theory]
    [InlineData("CreateMicrophone")]
    [InlineData("CreateSink")]
    [InlineData("CreateTranslationStream")]
    [InlineData("CreateConversationRecorder")]
    [InlineData("CreateTranscript")]
    public async Task FalhaNoMeioDaMontagemNaoVazaRecurso(string step)
    {
        var platform = new FakePlatform(failOn: step);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(platform));

        Assert.True(platform.AllDisposed,
            $"a falha em {step} deixou recursos abertos: " +
            $"{platform.Created.Count(c => !c.Disposed)} de {platform.Created.Count}");
    }

    [Fact]
    public async Task ConfiguracaoInvalidaNaoCriaNada()
    {
        var settings = Valid();
        settings.ApiKey = "";
        var platform = new FakePlatform();

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(platform, settings: settings));

        Assert.Empty(platform.Created);
    }

    [Fact]
    public async Task ATranscricaoRecebeOsDoisLados()
    {
        var platform = new FakePlatform();
        using var session = await StartAsync(platform);

        platform.Streams[0].EmitInputText("hello");
        platform.Streams[0].EmitOutputText("olá");
        platform.Streams[1].EmitInputText("tudo bem");
        platform.Streams[1].EmitOutputText("how are you");

        Assert.Contains("Eles [original]|hello", platform.Transcript);
        Assert.Contains("Eles [tradução]|olá", platform.Transcript);
        Assert.Contains("Você [original]|tudo bem", platform.Transcript);
        Assert.Contains("Você [tradução]|how are you", platform.Transcript);
    }

    [Fact]
    public async Task OContextoFicaTodoNoIdiomaDoUsuario()
    {
        var platform = new FakePlatform();
        var context = new ConversationContext();
        using var session = await TranslationSession.StartAsync(
            Valid(), new DeviceSourceChoice("cabo", "Cabo"), context, platform);

        platform.Streams[0].EmitInputText("what are you curious about?");
        platform.Streams[0].EmitOutputText("sobre o que você tem curiosidade?");
        platform.Streams[1].EmitInputText("sobre WebAssembly");
        platform.Streams[1].EmitOutputText("about WebAssembly");

        var recent = context.GetRecent();

        Assert.Contains("Eles: sobre o que você tem curiosidade?", recent);
        Assert.Contains("Você: sobre WebAssembly", recent);
        Assert.DoesNotContain("what are you curious about?", recent);
        Assert.DoesNotContain("about WebAssembly", recent);
    }

    [Fact]
    public async Task OAudioCapturadoVaiParaOModeloEParaAVozOriginal()
    {
        var platform = new FakePlatform();
        using var session = await StartAsync(platform);

        platform.Entrada!.EmitChunk(TestSignals.Constant(4800, 0.3f));

        Assert.Single(platform.Streams[0].Sent);
        Assert.Single(platform.Sinks[0].Originals);
    }

    [Fact]
    public async Task MutarAvisaOServidorUmaVezSo()
    {
        var platform = new FakePlatform();
        using var session = await StartAsync(platform);

        session.Outgoing.Muted = true;
        session.Outgoing.Muted = true;

        Assert.Equal(1, platform.Streams[1].StreamEnds);
        Assert.True(platform.Microphone!.Muted);
    }

    [Fact]
    public async Task DesmutarNaoAvisaOServidor()
    {
        var platform = new FakePlatform();
        using var session = await StartAsync(platform);

        session.Outgoing.Muted = true;
        session.Outgoing.Muted = false;

        Assert.Equal(1, platform.Streams[1].StreamEnds);
        Assert.False(platform.Microphone!.Muted);
    }

    private static Task<TranslationSession> StartAsync(
        FakePlatform platform,
        AudioSourceChoice? source = null,
        Settings? settings = null) =>
        TranslationSession.StartAsync(
            settings ?? Valid(),
            source ?? new DeviceSourceChoice("cabo", "Cabo"),
            new ConversationContext(),
            platform);

    private static Settings Valid() => new()
    {
        ApiKey = "chave",
        HeadphonesDeviceId = "fone",
        MicDeviceId = "mic",
        VirtualMicDeviceId = "cabo-virtual"
    };
}
