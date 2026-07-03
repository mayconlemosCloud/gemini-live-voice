using System.Diagnostics;
using GeminiLiveTranslate.Logging;
using NAudio.Wave;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Records both translation streams into ONE stereo .wav laid out on a wall-clock timeline:
///   left  = incoming translation (what YOU heard, in your language),
///   right = outgoing translation (what the OTHER side heard, in their language).
///
/// A timer drains each channel by the amount of real time that has elapsed, padding silence
/// whenever a side is quiet, so the two voices stay time-aligned exactly as they happened in the
/// call — instead of the two channels sliding out of sync because one side spoke more than the
/// other. Both Gemini translation streams are 24 kHz mono PCM16, so no resampling is needed.
/// </summary>
public sealed class ConversationRecorder : IDisposable
{
    private const int Rate = 24000; // both translation streams are 24 kHz mono PCM16

    private readonly WaveFileWriter _writer;                 // 24 kHz 16-bit STEREO
    private readonly BufferedWaveProvider _left;             // incoming translation
    private readonly BufferedWaveProvider _right;            // outgoing translation
    private readonly System.Timers.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private long _writtenFrames;                             // stereo frames already written
    private byte[] _l = Array.Empty<byte>();
    private byte[] _r = Array.Empty<byte>();
    private bool _disposed;

    public string Path { get; }

    public ConversationRecorder(string path)
    {
        Path = path;
        _writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
        _left = NewChannel();
        _right = NewChannel();
        _timer = new System.Timers.Timer(100) { AutoReset = true };
        _timer.Elapsed += (_, _) => Flush();
        _timer.Start();
        Log.Info("Gravação", $"Gravando a conversa (estéreo: esq=entrada, dir=saída) em: {path}");
    }

    // ReadFully pads reads with silence, so a quiet side becomes silence rather than desync.
    private static BufferedWaveProvider NewChannel() =>
        new(new WaveFormat(Rate, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(30), DiscardOnBufferOverflow = true };

    /// <summary>Feed the incoming translation (what you heard): 24 kHz mono PCM16 → left channel.</summary>
    public void WriteIncoming(byte[] pcm24kMono)
    {
        if (_disposed) return;
        _left.AddSamples(pcm24kMono, 0, pcm24kMono.Length);
    }

    /// <summary>Feed the outgoing translation (what they heard): 24 kHz mono PCM16 → right channel.</summary>
    public void WriteOutgoing(byte[] pcm24kMono)
    {
        if (_disposed) return;
        _right.AddSamples(pcm24kMono, 0, pcm24kMono.Length);
    }

    private void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            WriteElapsed();
        }
    }

    /// <summary>Writes stereo frames up to the current wall-clock position. Caller holds <see cref="_lock"/>.</summary>
    private void WriteElapsed()
    {
        long targetFrames = _clock.ElapsedMilliseconds * Rate / 1000;
        int need = (int)(targetFrames - _writtenFrames);
        if (need <= 0) return;

        int bytes = need * 2; // 16-bit mono
        if (_l.Length < bytes) { _l = new byte[bytes]; _r = new byte[bytes]; }
        _left.Read(_l, 0, bytes);   // ReadFully → zero-padded when a side is quiet
        _right.Read(_r, 0, bytes);

        for (int i = 0; i < need; i++)
        {
            int s = i * 2;
            _writer.Write(_l, s, 2); // left frame
            _writer.Write(_r, s, 2); // right frame
        }
        _writtenFrames = targetFrames;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _timer.Stop(); _timer.Dispose(); } catch { }
        lock (_lock)
        {
            try { WriteElapsed(); } catch { }   // capture the final tail before closing
            try { _writer.Dispose(); } catch { } // finalizes the .wav header
        }
    }
}
