using System.IO;
using GeminiLiveTranslate.Audio;
using GeminiLiveTranslate.Gemini;
using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiLiveTranslate.Translation;

/// <summary>
/// One translation flow: capture endpoint -> Gemini translate session -> playback endpoint.
/// Used twice (incoming and outgoing) to build the full bidirectional bridge.
/// </summary>
public sealed class TranslationDirection : IDisposable
{
    private readonly AudioCaptureSource _source;
    private readonly GeminiLiveClient _client;
    private readonly DuckingOutput _player;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _passthrough;
    private readonly bool _manualActivity;
    private readonly object _recLock = new();
    private WaveFileWriter? _recorder;
    private bool _disposed;

    public string Name { get; }

    /// <summary>When true, the capture is muted (no audio sent to Gemini) — used for push-to-talk.</summary>
    public bool MicMuted { get => _source.Muted; set => _source.Muted = value; }

    /// <summary>Original volume (0..1) while the translation speaks; adjustable live from the UI.</summary>
    public float DuckLevel { get => _player.DuckLevel; set => _player.DuckLevel = value; }

    /// <summary>Push-to-talk pressed: open the turn (manual VAD) and unmute the mic.</summary>
    public async Task BeginTalkAsync()
    {
        if (_manualActivity)
            await _client.SendActivityAsync(start: true, _cts.Token);
        MicMuted = false;
    }

    /// <summary>Push-to-talk released: stop the mic and close the turn so the model finalizes and stops.</summary>
    public async Task EndTalkAsync()
    {
        MicMuted = true;
        if (_manualActivity)
            await _client.SendActivityAsync(start: false, _cts.Token);
    }

    /// <summary>The exact audio being played to the listener (post-mix, post-duck) as (buffer, offset, sampleCount) in <see cref="RenderFormat"/>. The recorder taps this so the recording matches what is actually heard.</summary>
    public event Action<float[], int, int>? RenderedAudio;
    /// <summary>Format of the samples delivered by <see cref="RenderedAudio"/>.</summary>
    public WaveFormat RenderFormat => _player.RenderFormat;
    /// <summary>Original captured audio (16 kHz mono PCM16) sent to Gemini — the untranslated voice. On the incoming side it streams continuously; on the outgoing side only while you hold push-to-talk. Used by the conversation recorder.</summary>
    public event Action<byte[]>? CapturedAudio;
    /// <summary>Translated text the listener will hear (output transcript).</summary>
    public event Action<string>? TranslatedText;
    /// <summary>Original recognized text (input transcript).</summary>
    public event Action<string>? OriginalText;
    public event Action<float>? InputLevel;
    public event Action<string>? Status;
    /// <summary>(promptTokens, responseTokens, totalTokens) reported by the API.</summary>
    public event Action<long, long, long>? Usage;

    public TranslationDirection(
        string name,
        MMDevice inputDevice, bool loopback,
        MMDevice outputDevice,
        string apiKey, string model, string targetLang, bool echoTargetLanguage,
        bool continuousStreaming = true, bool duckOriginal = true,
        bool recordTranslation = false, bool manualActivity = false,
        float duckLevel = 0.18f,
        bool dropPlaybackBacklog = true, int maxPlaybackLatencyMs = 5000)
    {
        Name = name;
        _passthrough = duckOriginal;
        _manualActivity = manualActivity;
        _source = new AudioCaptureSource(inputDevice, loopback) { Tag = name, ContinuousMode = continuousStreaming };
        _player = new DuckingOutput(outputDevice)
        {
            Tag = name, PassthroughOriginal = duckOriginal, DuckLevel = duckLevel,
            DropBacklog = dropPlaybackBacklog, MaxLatencyMs = maxPlaybackLatencyMs
        };
        _client = new GeminiLiveClient(apiKey, model, targetLang, echoTargetLanguage, manualActivity) { Tag = name };

        if (recordTranslation) TryStartRecorder();

        _client.AudioReceived += OnTranslationAudio;
        _client.Interrupted += () => _player.ClearTranslation();
        _client.UsageUpdated += (p, r, t) => Usage?.Invoke(p, r, t);
        _client.OutputTranscript += t => TranslatedText?.Invoke(t);
        _client.InputTranscript += t => OriginalText?.Invoke(t);
        _client.StatusChanged += s => Status?.Invoke($"{name}: {s}");
        _client.ErrorOccurred += e => Status?.Invoke($"{name} — erro: {e}");

        _source.LevelChanged += l => InputLevel?.Invoke(l);
        _source.ChunkAvailable += OnChunk;
        _player.SamplesRendered += (b, o, n) => RenderedAudio?.Invoke(b, o, n);
    }

    private async void OnChunk(byte[] chunk)
    {
        // Outgoing (my voice → the meeting) is a clean mirror of the incoming path: capture →
        // Gemini → play ONLY the translation. The original passthrough exists solely for the
        // incoming "Google-Meet" effect (hear the other person quietly under the translation);
        // on the outgoing side it must never play, or the meeting would hear your untranslated
        // voice. No "mute while translating" guard either: Gemini translates simultaneously
        // (emits while you speak), so muting during playback would clip your sentence — the
        // loop is prevented by keeping capture and playback on different devices.
        CapturedAudio?.Invoke(chunk);
        if (_passthrough)
            _player.EnqueueOriginal(chunk);
        try { await _client.SendAudioAsync(chunk, _cts.Token); }
        catch { /* swallowed; surfaced via Status on the send path */ }
    }

    /// <summary>Plays the translated 24 kHz PCM and, if recording, writes it untouched to a .wav.</summary>
    private void OnTranslationAudio(byte[] pcm24k)
    {
        _player.EnqueueTranslation(pcm24k);
        lock (_recLock)
        {
            try { _recorder?.Write(pcm24k, 0, pcm24k.Length); } catch { }
        }
    }

    /// <summary>
    /// Records the exact audio Gemini returns (24 kHz mono PCM16) to a .wav in the log folder,
    /// before VoiceMeeter / the meeting app touch it. Lets us prove whether any robotic/choppy
    /// sound is produced by the app or added later in the routing chain.
    /// </summary>
    private void TryStartRecorder()
    {
        try
        {
            string dir = string.IsNullOrEmpty(Log.LogFolder) ? Path.GetTempPath() : Log.LogFolder;
            string path = Path.Combine(dir, $"traducao-{Name}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _recorder = new WaveFileWriter(path, new WaveFormat(24000, 16, 1));
            Log.Info(Name, $"Gravando a tradução pura (pré-VoiceMeeter) em: {path}");
        }
        catch (Exception ex) { Log.Error(Name, "Não foi possível iniciar a gravação .wav", ex); }
    }

    public async Task StartAsync()
    {
        Log.Info(Name, "Iniciando direção…");
        _player.Start();
        await _client.ConnectAsync(_cts.Token);
        _source.Start();
        Log.Info(Name, "Direção iniciada.");
    }

    public async Task StopAsync()
    {
        Log.Info(Name, "Parando direção…");
        try { _cts.Cancel(); } catch { }
        await _client.CloseAsync();
        Dispose();
        Log.Info(Name, "Direção parada.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _player.Dispose(); } catch { }
        lock (_recLock)
        {
            try { _recorder?.Dispose(); } catch { }   // finalizes the .wav header
            _recorder = null;
        }
        try { _cts.Dispose(); } catch { }
    }
}
