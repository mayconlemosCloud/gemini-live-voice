using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateV2;

/// <summary>
/// Records the whole session into ONE stereo .wav on a wall-clock timeline:
///   left  = what YOU hear   (Entrada's real output: their translation + their voice underneath),
///   right = what THEY hear  (Saída's real output: your translation + your voice underneath).
/// Nothing is re-mixed: each channel taps the exact samples its AudioOut rendered (RenderTap),
/// so the recording is identical to the live audio. Ported from GeminiLiveTranslate's
/// ConversationRecorder, minus that app's separate mic bus — in V2 the original voice is
/// already part of each AudioOut's mix, so a pure player tap per side is the whole story.
/// A timer advances both channels by real elapsed time so they stay aligned; ReadFully pads
/// silence when a side is momentarily quiet.
/// </summary>
public sealed class ConversationRecorder : IDisposable
{
    private const int Rate = 24000; // output rate of the stereo .wav

    private readonly WaveFileWriter _writer; // 24 kHz 16-bit STEREO
    private readonly Side _left;             // Entrada (what you hear)
    private readonly Side _right;            // Saída (what they hear)
    private readonly System.Timers.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private long _writtenFrames;
    private float[] _l = Array.Empty<float>();
    private float[] _r = Array.Empty<float>();
    private byte[] _out = Array.Empty<byte>();
    private volatile bool _disposed;

    public string Path { get; }

    public ConversationRecorder(string path, WaveFormat incomingMixFormat, WaveFormat outgoingMixFormat)
    {
        Path = path;
        _writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
        _left = new Side(incomingMixFormat);
        _right = new Side(outgoingMixFormat);
        _timer = new System.Timers.Timer(100) { AutoReset = true };
        _timer.Elapsed += (_, _) => Flush();
        _timer.Start();
        Log.Write("Gravação", $"gravando a conversa (estéreo: esq=você ouve, dir=eles ouvem) em: {path}");
    }

    /// <summary>Entrada's rendered output → left channel. Called on the render thread.</summary>
    public void WriteIncoming(float[] buffer, int offset, int count) { if (!_disposed) _left.Add(buffer, offset, count); }
    /// <summary>Saída's rendered output → right channel. Called on the render thread.</summary>
    public void WriteOutgoing(float[] buffer, int offset, int count) { if (!_disposed) _right.Add(buffer, offset, count); }

    private void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            WriteElapsed();
        }
    }

    /// <summary>Reads and writes stereo frames up to the current wall-clock position. Caller holds <see cref="_lock"/>.</summary>
    private void WriteElapsed()
    {
        long targetFrames = _clock.ElapsedMilliseconds * Rate / 1000;
        int need = (int)(targetFrames - _writtenFrames);
        if (need <= 0) return;

        if (_l.Length < need)
        {
            _l = new float[need];
            _r = new float[need];
            _out = new byte[need * 4]; // stereo, 16-bit
        }
        _left.Read(_l, need);
        _right.Read(_r, need);

        int b = 0;
        for (int i = 0; i < need; i++)
        {
            short l = ToPcm(_l[i]);
            short r = ToPcm(_r[i]);
            _out[b++] = (byte)(l & 0xFF); _out[b++] = (byte)((l >> 8) & 0xFF);
            _out[b++] = (byte)(r & 0xFF); _out[b++] = (byte)((r >> 8) & 0xFF);
        }
        _writer.Write(_out, 0, b);
        _writtenFrames = targetFrames;
    }

    private static short ToPcm(float s)
    {
        s = s > 1f ? 1f : s < -1f ? -1f : s; // translation + original can sum past full scale
        return (short)(s * 32767f);
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
            try { WriteElapsed(); } catch { }    // capture the final tail
            try { _writer.Dispose(); } catch { } // finalizes the .wav header
        }
        Log.Write("Gravação", "conversa gravada e finalizada.");
    }

    /// <summary>One channel: a player tap (device mix format) down-mixed to mono and resampled to <see cref="Rate"/>.</summary>
    private sealed class Side
    {
        private readonly BufferedWaveProvider _in; // IEEE float at the device mix format
        private readonly ISampleProvider _src;     // → mono @ Rate
        private readonly object _addLock = new();
        private byte[] _rb = Array.Empty<byte>();

        public Side(WaveFormat mixFormat)
        {
            _in = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, mixFormat.Channels))
            { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(30), DiscardOnBufferOverflow = true };
            ISampleProvider sp = _in.ToSampleProvider();
            if (mixFormat.Channels > 1) sp = new AnyToMono(sp);
            if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
            _src = sp;
        }

        public void Add(float[] buffer, int offset, int count)
        {
            lock (_addLock)
            {
                int bytes = count * 4; // 32-bit float
                if (_rb.Length < bytes) _rb = new byte[bytes];
                Buffer.BlockCopy(buffer, offset * 4, _rb, 0, bytes);
                _in.AddSamples(_rb, 0, bytes);
            }
        }

        /// <summary>Fills dst[0..count) with this channel's audio, silence-padded when empty.</summary>
        public void Read(float[] dst, int count) => _src.Read(dst, 0, count);
    }
}

/// <summary>Averages any number of channels down to mono.</summary>
internal sealed class AnyToMono : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _channels;
    private float[] _buf = Array.Empty<float>();

    public AnyToMono(ISampleProvider src)
    {
        _src = src;
        _channels = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_buf.Length < needed) _buf = new float[needed];
        int got = _src.Read(_buf, 0, needed);
        int frames = got / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _buf[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
