namespace GeminiTranslateV2;

/// <summary>Common shape for both capture sources (real mic vs. process loopback) so Direction can treat them uniformly.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Sample rate of the emitted chunks (native rate — never resampled for quality).</summary>
    int SampleRate { get; }

    /// <summary>Mono PCM16 chunk, exactly as captured — fired for every chunk, unconditionally. No local silence gate: the server's own VAD owns turn detection.</summary>
    event Action<byte[]>? ChunkAvailable;

    /// <summary>RMS 0..1, ~10x/s, for a VU meter.</summary>
    event Action<float>? Level;

    bool Muted { get; set; }

    Task StartAsync();
}
