using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiTranslateV2;

/// <summary>
/// One translation flow: capture → Gemini → playback, with the original voice mixed low
/// under the translation. "Entrada" uses ProcessCapture or LoopbackCapture (the call app's
/// audio). "Saída" uses MicCapture (your real mic, with Windows' own noise suppression enabled).
/// </summary>
public sealed class Direction : IDisposable
{
    private readonly IAudioSource _in;
    private readonly AudioOut _out;
    private readonly LiveClient _client;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private readonly WaveFileWriter? _sentWav;
    private readonly WaveFileWriter? _recvWav;
    private readonly object _wavLock = new();

    public string Name { get; }

    public event Action<string>? OriginalText;
    public event Action<string>? TranslatedText;
    public event Action<float>? Level;
    public event Action<string>? Status;

    /// <summary>Muting is the only local "stream paused" signal we send — never a guessed silence timeout.</summary>
    public bool Muted
    {
        get => _in.Muted;
        set
        {
            if (value && !_in.Muted) _ = _client.SendAudioStreamEndAsync(_cts.Token);
            _in.Muted = value;
        }
    }

    public float OriginalVolume { set => _out.OriginalVolume = value; }

    /// <summary>Format of this direction's rendered mix, for ConversationRecorder.</summary>
    public WaveFormat OutputMixFormat => _out.MixFormat;

    /// <summary>Translated audio queued for playback — the live delay the listener hears.</summary>
    public TimeSpan TranslationQueue => _out.TranslationQueue;

    /// <summary>True while the queue got long and the translation is playing at 1.1×.</summary>
    public bool CatchingUp => _out.CatchingUp;

    /// <summary>Tap on this direction's rendered mix (exactly what its listener hears).</summary>
    public Action<float[], int, int>? OutputTap { set => _out.RenderTap = value; }

    public Direction(string name, IAudioSource inputSource, MMDevice outputDevice,
        string apiKey, string model, string targetLang, float originalVolume)
    {
        Name = name;
        _in = inputSource;
        _out = new AudioOut(outputDevice, _in.SampleRate, originalVolume, name);
        _client = new LiveClient(apiKey, model, targetLang, _in.SampleRate, name);

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _sentWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-enviado-{stamp}.wav"),
                new WaveFormat(_in.SampleRate, 16, 1));
            _recvWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-recebido-{stamp}.wav"),
                new WaveFormat(24000, 16, 1));
        }
        catch (Exception ex) { Log.Write(name, "sem gravação de diagnóstico: " + ex.Message); }

        _in.ChunkAvailable += chunk =>
        {
            _out.EnqueueOriginal(chunk); // the original voice always flows
            lock (_wavLock) { try { _sentWav?.Write(chunk, 0, chunk.Length); } catch { } }
            _ = _client.SendAudioAsync(chunk, _cts.Token); // always forwarded — server owns VAD
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
        await _in.StartAsync();
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
