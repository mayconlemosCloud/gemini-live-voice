using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.Core.Tests;

/// <summary>
/// Plataforma de mentira que registra tudo o que foi criado, iniciado e descartado.
/// </summary>
/// <remarks>
/// É o que a arquitetura hexagonal comprou: a montagem inteira de uma sessão — incluindo o
/// caminho de erro, em que uma falha no meio precisa desfazer o que já foi criado — passa a ser
/// verificável sem placa de som, sem rede e sem chave de API.
/// </remarks>
internal sealed class FakePlatform : ITranslationPlatform
{
    private readonly string? _failOn;

    /// <param name="failOn">Nome do artefato cuja criação deve falhar, para exercitar o rollback.</param>
    public FakePlatform(string? failOn = null) => _failOn = failOn;

    public List<FakeDisposable> Created { get; } = [];

    public List<string> Transcript { get; } = [];

    public string? DefaultDevicesEntradaId { get; private set; }

    public bool DefaultDevicesApplied { get; private set; }

    public FakeAudioSource? Entrada { get; private set; }

    public FakeAudioSource? Microphone { get; private set; }

    public List<FakeTranslationStream> Streams { get; } = [];

    public List<FakeAudioSink> Sinks { get; } = [];

    public bool AllDisposed => Created.All(c => c.Disposed);

    public (IAudioSource Source, string Label) CreateEntradaSource(AudioSourceChoice choice)
    {
        Fail(nameof(CreateEntradaSource));
        Entrada = Track(new FakeAudioSource());
        return (Entrada, choice.Label);
    }

    public IAudioSource CreateMicrophone(string deviceId)
    {
        Fail(nameof(CreateMicrophone));
        Microphone = Track(new FakeAudioSource());
        return Microphone;
    }

    public IAudioSink CreateSink(string deviceId, int originalRate, float originalVolume, bool catchUp, string tag)
    {
        Fail(nameof(CreateSink));
        var sink = Track(new FakeAudioSink());
        Sinks.Add(sink);
        return sink;
    }

    public ITranslationStream CreateTranslationStream(
        string apiKey, string model, string targetLang, int inputRate, string tag)
    {
        Fail(nameof(CreateTranslationStream));
        var stream = Track(new FakeTranslationStream());
        Streams.Add(stream);
        return stream;
    }

    public IResampler CreateWireResampler(int inputRate) => new PassthroughResampler();

    public IAssistant CreateAssistant(string apiKey, string model, string persona)
    {
        Fail(nameof(CreateAssistant));
        return Track(new FakeAssistant());
    }

    public IDiagnosticRecorder CreateDiagnostics(string name)
    {
        Fail(nameof(CreateDiagnostics));
        return Track(new FakeDiagnosticRecorder());
    }

    public IConversationRecorder CreateConversationRecorder(
        string stamp, AudioFormat incoming, AudioFormat outgoing)
    {
        Fail(nameof(CreateConversationRecorder));
        return Track(new FakeConversationRecorder());
    }

    public ITranscriptSink CreateTranscript(string stamp)
    {
        Fail(nameof(CreateTranscript));
        return Track(new FakeTranscriptSink(Transcript));
    }

    public DefaultDevicesResult ApplyDefaultDevices(Settings settings, string? entradaDeviceId)
    {
        DefaultDevicesApplied = true;
        DefaultDevicesEntradaId = entradaDeviceId;
        return new DefaultDevicesResult(Track(new FakeDisposable()), "padrões aplicados");
    }

    private T Track<T>(T item) where T : FakeDisposable
    {
        Created.Add(item);
        return item;
    }

    private void Fail(string step)
    {
        if (_failOn == step) throw new InvalidOperationException($"falha simulada em {step}");
    }
}

/// <summary>Base dos dublês, com registro de descarte.</summary>
internal class FakeDisposable : IDisposable
{
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeAudioSource : FakeDisposable, IAudioSource
{
    public int SampleRate => 48000;

    public bool Muted { get; set; }

    public bool Started { get; private set; }

    public event Action<byte[]>? ChunkAvailable;

    public event Action<float>? Level;

    public Task StartAsync()
    {
        Started = true;
        return Task.CompletedTask;
    }

    public void EmitChunk(byte[] pcm) => ChunkAvailable?.Invoke(pcm);

    public void EmitLevel(float level) => Level?.Invoke(level);
}

internal sealed class FakeAudioSink : FakeDisposable, IAudioSink
{
    public TimeSpan TranslationQueue => TimeSpan.Zero;

    public double CatchUpSpeed => 1.0;

    public AudioFormat MixFormat => new(48000, 2);

    public float OriginalVolume { get; set; }

    public Action<float[], int, int>? RenderTap { get; set; }

    public bool Started { get; private set; }

    public List<byte[]> Translations { get; } = [];

    public List<byte[]> Originals { get; } = [];

    public void Start() => Started = true;

    public void EnqueueTranslation(byte[] pcm) => Translations.Add(pcm);

    public void EnqueueOriginal(byte[] pcm) => Originals.Add(pcm);
}

internal sealed class FakeTranslationStream : FakeDisposable, ITranslationStream
{
    public event Action<byte[]>? AudioReceived;

    public event Action<string>? InputText;

    public event Action<string>? OutputText;

    public event Action<string>? Status;

    public int OutboxBacklog => 0;

    public bool Connected { get; private set; }

    public List<byte[]> Sent { get; } = [];

    public int StreamEnds { get; private set; }

    public Task ConnectAsync(CancellationToken ct)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public void EnqueueAudio(byte[] pcm) => Sent.Add(pcm);

    public void EnqueueAudioStreamEnd() => StreamEnds++;

    public void EmitAudio(byte[] pcm) => AudioReceived?.Invoke(pcm);

    public void EmitInputText(string text) => InputText?.Invoke(text);

    public void EmitOutputText(string text) => OutputText?.Invoke(text);

    public void EmitStatus(string status) => Status?.Invoke(status);
}

/// <summary>Devolve o chunk sem alterar nada — a conversão de taxa não é o alvo destes testes.</summary>
internal sealed class PassthroughResampler : IResampler
{
    public byte[]? Feed(byte[] chunk) => chunk;
}

internal sealed class FakeAssistant : FakeDisposable, IAssistant
{
    public Task<string> SuggestAnswerAsync(string question, string context, CancellationToken ct) =>
        Task.FromResult("resposta");

    public Task<string> SuggestFromConversationAsync(string context, CancellationToken ct) =>
        Task.FromResult("sugestão");

    public Task<string> ChatAsync(IReadOnlyList<ChatTurn> history, string context, CancellationToken ct) =>
        Task.FromResult("chat");

    public Task<string> AnalyzeImageAsync(byte[] png, string context, CancellationToken ct) =>
        Task.FromResult("imagem");
}

internal sealed class FakeDiagnosticRecorder : FakeDisposable, IDiagnosticRecorder
{
    public List<byte[]> Sent { get; } = [];

    public List<byte[]> Received { get; } = [];

    public void WriteSent(byte[] pcm) => Sent.Add(pcm);

    public void WriteReceived(byte[] pcm) => Received.Add(pcm);
}

internal sealed class FakeConversationRecorder : FakeDisposable, IConversationRecorder
{
    public void WriteIncoming(float[] buffer, int offset, int count)
    {
    }

    public void WriteOutgoing(float[] buffer, int offset, int count)
    {
    }
}

internal sealed class FakeTranscriptSink(List<string> lines) : FakeDisposable, ITranscriptSink
{
    public void Append(string label, string fragment) => lines.Add($"{label}|{fragment}");
}
