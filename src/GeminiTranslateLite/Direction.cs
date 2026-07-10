using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiTranslateLite;

/// <summary>
/// One translation flow: capture → Gemini → playback, with the original voice mixed low
/// under the translation. Used twice: incoming (meeting → my headphones) and outgoing
/// (my mic → virtual mic the meeting listens to).
/// </summary>
public sealed class Direction : IDisposable
{
    private readonly AudioIn _in;
    private readonly AudioOut _out;
    private readonly LiveClient _client;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // Diagnostic taps (no processing): exactly what the model HEARD and exactly what it
    // RETURNED, straight off the wire — lets us tell "the model produced an unnatural voice"
    // apart from "the routing/meeting app degraded it". Files land next to the session log.
    private readonly WaveFileWriter? _sentWav;
    private readonly WaveFileWriter? _recvWav;
    private readonly object _wavLock = new();

    public string Name { get; }

    public event Action<string>? OriginalText;
    public event Action<string>? TranslatedText;
    public event Action<float>? Level;
    public event Action<string>? Status;

    public bool Muted { get => _in.Muted; set => _in.Muted = value; }
    public float OriginalVolume { set => _out.OriginalVolume = value; }

    public Direction(string name, MMDevice inputDevice, bool loopback, MMDevice outputDevice,
        string apiKey, string model, string targetLang, float originalVolume)
    {
        Name = name;
        _in = new AudioIn(inputDevice, loopback, name);
        _out = new AudioOut(outputDevice, originalVolume, name);
        _client = new LiveClient(apiKey, model, targetLang, name);

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _sentWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-enviado-{stamp}.wav"),
                new WaveFormat(16000, 16, 1));
            _recvWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-recebido-{stamp}.wav"),
                new WaveFormat(24000, 16, 1));
        }
        catch (Exception ex) { Log.Write(name, "sem gravação de diagnóstico: " + ex.Message); }

        _in.ChunkAvailable += (chunk, sendToModel) =>
        {
            _out.EnqueueOriginal(chunk); // the original voice always flows
            if (!sendToModel) return;    // silence stop applies to the model send only
            lock (_wavLock) { try { _sentWav?.Write(chunk, 0, chunk.Length); } catch { } }
            _ = _client.SendAudioAsync(chunk, _cts.Token);
        };
        _in.Level += l => Level?.Invoke(l);
        _client.AudioReceived += pcm =>
        {
            lock (_wavLock) { try { _recvWav?.Write(pcm, 0, pcm.Length); } catch { } }
            _out.EnqueueTranslation(pcm);
        };
        _client.InputText += t => OriginalText?.Invoke(t);
        _client.OutputText += t => TranslatedText?.Invoke(t);
        _client.Status += s => Status?.Invoke(s);
    }

    public async Task StartAsync()
    {
        _out.Start();
        await _client.ConnectAsync(_cts.Token);
        _in.Start();
        Log.Write(Name, "direção iniciada.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _in.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _out.Dispose(); } catch { }
        lock (_wavLock)
        {
            try { _sentWav?.Dispose(); } catch { }
            try { _recvWav?.Dispose(); } catch { }
        }
        try { _cts.Dispose(); } catch { }
        Log.Write(Name, "direção parada.");
    }
}
