using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;

namespace GeminiTranslate.Core.Session;

/// <summary>
/// Uma sessão de tradução ao vivo: as duas direções, o assistente opcional, a gravação, a
/// transcrição e os dispositivos padrão assumidos.
/// </summary>
/// <remarks>
/// Existe para que a janela principal não seja dona da mecânica da chamada. Antes, montar uma
/// sessão, validá-la, lidar com falha no meio da montagem e desmontar tudo na ordem certa eram
/// responsabilidade de um manipulador de clique — o que tornava impossível iniciar uma sessão
/// sem uma interface, e fácil vazar recursos num caminho de erro.
///
/// Tudo o que depende do sistema vem da <see cref="ITranslationPlatform"/>, então esta classe
/// não conhece áudio nativo, rede nem disco.
///
/// A instância só existe quando a sessão está no ar: se a montagem falhar, tudo o que já tinha
/// sido criado é descartado antes de a exceção subir.
/// </remarks>
public sealed class TranslationSession : IDisposable
{
    private readonly IConversationRecorder _recorder;
    private readonly ITranscriptSink _transcript;
    private readonly IDisposable? _defaultDevices;
    private bool _disposed;

    /// <summary>Direção que traduz o que a outra pessoa diz.</summary>
    public TranslationDirection Incoming { get; }

    /// <summary>Direção que traduz o que o usuário diz.</summary>
    public TranslationDirection Outgoing { get; }

    /// <summary>Assistente de respostas, ou null quando desligado ou sem chave.</summary>
    public IAssistant? Assistant { get; }

    /// <summary>Conversa acumulada dos dois lados, para as ações de IA.</summary>
    public ConversationContext Context { get; }

    /// <summary>Rótulo do que está sendo escutado, para o cabeçalho da interface.</summary>
    public string IncomingLabel { get; }

    /// <summary>Descrição do que aconteceu com os dispositivos padrão do sistema.</summary>
    public string DefaultDevicesNote { get; }

    private TranslationSession(
        TranslationDirection incoming,
        TranslationDirection outgoing,
        IAssistant? assistant,
        ConversationContext context,
        IConversationRecorder recorder,
        ITranscriptSink transcript,
        IDisposable? defaultDevices,
        string incomingLabel,
        string defaultDevicesNote)
    {
        Incoming = incoming;
        Outgoing = outgoing;
        Assistant = assistant;
        Context = context;
        IncomingLabel = incomingLabel;
        DefaultDevicesNote = defaultDevicesNote;
        _recorder = recorder;
        _transcript = transcript;
        _defaultDevices = defaultDevices;
    }

    /// <summary>
    /// Valida, monta e inicia uma sessão completa.
    /// </summary>
    /// <param name="settings">Preferências já sincronizadas com a interface.</param>
    /// <param name="source">O que a direção "Entrada" deve escutar.</param>
    /// <param name="context">Contexto de conversa a alimentar, reaproveitado entre sessões.</param>
    /// <param name="platform">Fábrica de tudo o que depende do sistema.</param>
    /// <exception cref="InvalidOperationException">Configuração inválida ou dispositivo indisponível.</exception>
    public static async Task<TranslationSession> StartAsync(
        Settings settings,
        AudioSourceChoice? source,
        ConversationContext context,
        ITranslationPlatform platform)
    {
        SessionValidator.Validate(settings, source);

        var builder = new SessionBuilder(settings, source!, context, platform);
        try
        {
            var session = builder.Build();
            await session.Incoming.StartAsync();
            await session.Outgoing.StartAsync();
            return session;
        }
        catch
        {
            builder.DisposePartial();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Incoming.Dispose(); } catch { }
        try { Outgoing.Dispose(); } catch { }
        try { _recorder.Dispose(); } catch { }
        try { _transcript.Dispose(); } catch { }
        try { Assistant?.Dispose(); } catch { }

        try { _defaultDevices?.Dispose(); }
        catch (Exception ex) { Log.Write("Padrão", "falha ao restaurar: " + ex); }
    }

    /// <summary>
    /// Monta as peças de uma sessão, guardando o que já criou para poder descartar tudo se a
    /// montagem falhar no meio.
    /// </summary>
    private sealed class SessionBuilder(
        Settings settings,
        AudioSourceChoice source,
        ConversationContext context,
        ITranslationPlatform platform)
    {
        private readonly List<IDisposable> _created = [];

        /// <summary>Descarta, em ordem inversa, tudo o que já havia sido criado.</summary>
        public void DisposePartial()
        {
            for (int i = _created.Count - 1; i >= 0; i--)
                try { _created[i].Dispose(); } catch { }
            _created.Clear();
        }

        public TranslationSession Build()
        {
            var (entrada, label) = platform.CreateEntradaSource(source);
            _created.Add(entrada);

            var defaults = ApplyDefaultDevices();
            if (defaults.Scope is not null) _created.Add(defaults.Scope);

            var assistant = CreateAssistant();
            if (assistant is not null) _created.Add(assistant);

            context.Clear();

            var microphone = Track(platform.CreateMicrophone(settings.MicDeviceId!));

            var incoming = BuildDirection("Entrada", settings.MyLang, entrada,
                settings.HeadphonesDeviceId!);
            var outgoing = BuildDirection("Saída", settings.TheirLang, microphone,
                settings.VirtualMicDeviceId!);

            var (recorder, transcript) = CreateLogs(incoming, outgoing);
            WireContext(incoming, outgoing);

            return new TranslationSession(incoming, outgoing, assistant, context,
                recorder, transcript, defaults.Scope, label, defaults.Note);
        }

        /// <summary>
        /// Alimenta o contexto do assistente com a conversa INTEIRA NO IDIOMA DO USUÁRIO.
        /// </summary>
        /// <remarks>
        /// Os dois lados precisam chegar no mesmo idioma, e é por isso que cada um usa uma fonte
        /// diferente: de "Eles" vale a TRADUÇÃO (o que a outra pessoa disse, já no idioma do
        /// usuário), e de "Você" vale o ORIGINAL (o que o usuário realmente falou).
        ///
        /// Usar a tradução dos dois lados era o defeito anterior: as falas do próprio usuário
        /// entravam no contexto já vertidas para o idioma da outra pessoa, e o modelo respondia
        /// naquele idioma — imitando o padrão da transcrição, que pesa mais que a instrução de
        /// sistema. Uma pergunta em português rendia uma sugestão em inglês.
        /// </remarks>
        private void WireContext(TranslationDirection incoming, TranslationDirection outgoing)
        {
            incoming.TranslatedText += text => context.Add("Eles", text);
            outgoing.OriginalText += text => context.Add("Você", text);
        }

        /// <summary>Reúne as peças de uma direção e as registra para descarte em caso de falha.</summary>
        private TranslationDirection BuildDirection(
            string name, string targetLang, IAudioSource input, string outputDeviceId)
        {
            var options = new DirectionOptions(name, settings.ApiKey, settings.Model, targetLang,
                (float)settings.OriginalVolume, settings.CatchUpEnabled);

            var sink = Track(platform.CreateSink(outputDeviceId, input.SampleRate,
                options.OriginalVolume, options.CatchUp, name));
            var stream = Track(platform.CreateTranslationStream(options.ApiKey, options.Model,
                options.TargetLang, AudioRates.Wire, name));
            var diagnostics = Track(platform.CreateDiagnostics(name));
            var resampler = platform.CreateWireResampler(input.SampleRate);

            return Track(new TranslationDirection(options, input, sink, stream, resampler, diagnostics));
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _created.Add(disposable);
            return disposable;
        }

        /// <summary>
        /// Assume os cabos como padrão do sistema, quando pedido, para que os apps de chamada
        /// peguem o áudio certo sozinhos.
        /// </summary>
        private DefaultDevicesResult ApplyDefaultDevices()
        {
            if (!settings.MakeCablesDefault) return new DefaultDevicesResult(null, "");

            return platform.ApplyDefaultDevices(settings, (source as DeviceSourceChoice)?.DeviceId);
        }

        /// <summary>Cria o assistente apenas quando ele está ligado e há chave preenchida.</summary>
        private IAssistant? CreateAssistant()
        {
            bool wanted = settings.AssistantEnabled;
            bool hasKey = !string.IsNullOrWhiteSpace(settings.ApiKey);
            var assistant = wanted && hasKey
                ? platform.CreateAssistant(settings.ApiKey, settings.AssistantModel, settings.AssistantContext)
                : null;

            Log.Write("Assistente", $"configuração: marcado={wanted} · chave={(hasKey ? "ok" : "vazia")} " +
                                    $"· modelo={settings.AssistantModel} · ativo={assistant is not null}");
            return assistant;
        }

        /// <summary>
        /// Cria o registro completo da conversa: um áudio estéreo com o que cada lado ouviu, e um
        /// texto com original e tradução das duas direções.
        /// </summary>
        private (IConversationRecorder, ITranscriptSink) CreateLogs(
            TranslationDirection incoming, TranslationDirection outgoing)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var recorder = Track(platform.CreateConversationRecorder(stamp,
                incoming.OutputMixFormat, outgoing.OutputMixFormat));
            incoming.OutputTap = recorder.WriteIncoming;
            outgoing.OutputTap = recorder.WriteOutgoing;

            var transcript = Track(platform.CreateTranscript(stamp));
            WireTranscript(incoming, transcript, "Eles");
            WireTranscript(outgoing, transcript, "Você");

            return (recorder, transcript);
        }

        private static void WireTranscript(
            TranslationDirection direction, ITranscriptSink transcript, string who)
        {
            direction.OriginalText += text => transcript.Append($"{who} [original]", text);
            direction.TranslatedText += text => transcript.Append($"{who} [tradução]", text);
        }
    }
}
