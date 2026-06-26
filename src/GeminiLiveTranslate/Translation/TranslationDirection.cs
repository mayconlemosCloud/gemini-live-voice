using GeminiLiveTranslate.Audio;
using GeminiLiveTranslate.Gemini;
using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;

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
    private bool _disposed;

    public string Name { get; }

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
        bool continuousStreaming = true, bool duckOriginal = true)
    {
        Name = name;
        _source = new AudioCaptureSource(inputDevice, loopback) { Tag = name, ContinuousMode = continuousStreaming };
        _player = new DuckingOutput(outputDevice) { Tag = name, PassthroughOriginal = duckOriginal };
        _client = new GeminiLiveClient(apiKey, model, targetLang, echoTargetLanguage) { Tag = name };

        _client.AudioReceived += pcm => _player.EnqueueTranslation(pcm);
        _client.Interrupted += () => _player.ClearTranslation();
        _client.UsageUpdated += (p, r, t) => Usage?.Invoke(p, r, t);
        _client.OutputTranscript += t => TranslatedText?.Invoke(t);
        _client.InputTranscript += t => OriginalText?.Invoke(t);
        _client.StatusChanged += s => Status?.Invoke($"{name}: {s}");
        _client.ErrorOccurred += e => Status?.Invoke($"{name} — erro: {e}");

        _source.LevelChanged += l => InputLevel?.Invoke(l);
        _source.ChunkAvailable += OnChunk;
    }

    private async void OnChunk(byte[] chunk)
    {
        // Passthrough the original so the player can play it (ducked) under the translation.
        _player.EnqueueOriginal(chunk);
        try { await _client.SendAudioAsync(chunk, _cts.Token); }
        catch { /* swallowed; surfaced via Status on the send path */ }
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
        try { _cts.Dispose(); } catch { }
    }
}
