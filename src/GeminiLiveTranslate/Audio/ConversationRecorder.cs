using System.Diagnostics;
using GeminiLiveTranslate.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Records the live audio into ONE stereo .wav on a wall-clock timeline:
///   left  = what YOU hear (the incoming player's output — ducked original + translation),
///   right = what the OTHER side hears (the outgoing player's translation + your mic).
///
/// It does NOT re-mix anything: each channel is tapped straight from the player that is already
/// playing it, so the recording is identical to the live audio — same ducking, same smoothing.
/// The players deliver samples at their device mix format; each side is down-mixed to mono and
/// resampled to 24 kHz. The outgoing side also sums your captured mic voice (16 kHz), because the
/// meeting hears both your translation (from the cable) and your original voice at once.
///
/// A timer advances both channels by the real time elapsed so they stay aligned; ReadFully pads
/// silence when a source is momentarily empty.
/// </summary>
public sealed class ConversationRecorder : IDisposable
{
    private const int Rate = 24000;     // output rate
    private const int OrigRate = 16000; // captured mic voice is 16 kHz mono

    private readonly WaveFileWriter _writer; // 24 kHz 16-bit STEREO
    private readonly Side _left;             // incoming (what you hear)
    private readonly Side? _right;           // outgoing (what they hear); null if outgoing is off
    private readonly System.Timers.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private long _writtenFrames;             // stereo frames already written
    private float[] _l = Array.Empty<float>();
    private float[] _r = Array.Empty<float>();
    private byte[] _out = Array.Empty<byte>();
    private bool _disposed;

    public string Path { get; }

    public ConversationRecorder(string path, WaveFormat incomingFormat, WaveFormat? outgoingFormat)
    {
        Path = path;
        _writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
        _left = new Side(incomingFormat, withOriginal: false);
        _right = outgoingFormat is null ? null : new Side(outgoingFormat, withOriginal: true);
        _timer = new System.Timers.Timer(100) { AutoReset = true };
        _timer.Elapsed += (_, _) => Flush();
        _timer.Start();
        Log.Info("Gravação", $"Gravando a conversa ao vivo (estéreo: esq=você ouve, dir=eles ouvem) em: {path}");
    }

    /// <summary>Incoming player output (what you hear) → left channel. Called on the render thread.</summary>
    public void WriteIncoming(float[] buffer, int offset, int count) { if (!_disposed) _left.AddRendered(buffer, offset, count); }
    /// <summary>Outgoing player output (the translation they hear) → right channel. Render thread.</summary>
    public void WriteOutgoing(float[] buffer, int offset, int count) { if (!_disposed) _right?.AddRendered(buffer, offset, count); }
    /// <summary>Your captured mic voice (16 kHz mono PCM16) → summed into the right channel.</summary>
    public void WriteOutgoingOriginal(byte[] pcm16kMono) { if (!_disposed) _right?.AddOriginal(pcm16kMono); }

    private void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            WriteElapsed();
        }
    }

    /// <summary>Reads, mixes and writes stereo frames up to the current wall-clock position. Caller holds <see cref="_lock"/>.</summary>
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
        if (_right is not null) _right.Read(_r, need); else Array.Clear(_r, 0, need);

        int b = 0;
        for (int i = 0; i < need; i++)
        {
            short l = ToPcm(_l[i]);
            short r = ToPcm(_r[i]);
            _out[b++] = (byte)(l & 0xFF); _out[b++] = (byte)((l >> 8) & 0xFF); // left frame
            _out[b++] = (byte)(r & 0xFF); _out[b++] = (byte)((r >> 8) & 0xFF); // right frame
        }
        _writer.Write(_out, 0, b);
        _writtenFrames = targetFrames;
    }

    private static short ToPcm(float s)
    {
        s = s > 1f ? 1f : s < -1f ? -1f : s; // clamp before quantizing (mic + translation can sum > 1)
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
            try { WriteElapsed(); } catch { }   // capture the final tail before closing
            try { _writer.Dispose(); } catch { } // finalizes the .wav header
        }
    }

    /// <summary>
    /// One recorded channel: a player tap (device mix format) down-mixed to mono and resampled to
    /// <see cref="Rate"/>, optionally summed with your captured mic voice. Everything is already
    /// smooth (the player did the ducking/jitter-buffering), so this only converts and mixes.
    /// </summary>
    private sealed class Side
    {
        private readonly BufferedWaveProvider _in;   // IEEE float at the device mix format
        private readonly ISampleProvider _src;        // → mono, Rate
        private readonly object _addLock = new();
        private byte[] _rb = Array.Empty<byte>();

        private readonly BufferedWaveProvider? _origBuf; // your mic voice, 16 kHz PCM16
        private readonly ISampleProvider? _origSrc;       // → Rate
        private float[] _os = Array.Empty<float>();

        public Side(WaveFormat deviceFormat, bool withOriginal)
        {
            _in = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(deviceFormat.SampleRate, deviceFormat.Channels))
            { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(30), DiscardOnBufferOverflow = true };
            ISampleProvider sp = _in.ToSampleProvider();
            if (deviceFormat.Channels > 1) sp = new ToMonoSampleProvider(sp);
            if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
            _src = sp;

            if (withOriginal)
            {
                _origBuf = new BufferedWaveProvider(new WaveFormat(OrigRate, 16, 1))
                { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(30), DiscardOnBufferOverflow = true };
                ISampleProvider o = _origBuf.ToSampleProvider();
                _origSrc = o.WaveFormat.SampleRate != Rate ? new WdlResamplingSampleProvider(o, Rate) : o;
            }
        }

        /// <summary>Feed a block of played samples (device mix format) into this channel.</summary>
        public void AddRendered(float[] buffer, int offset, int count)
        {
            lock (_addLock)
            {
                int bytes = count * 4; // 32-bit float
                if (_rb.Length < bytes) _rb = new byte[bytes];
                Buffer.BlockCopy(buffer, offset * 4, _rb, 0, bytes);
                _in.AddSamples(_rb, 0, bytes);
            }
        }

        public void AddOriginal(byte[] pcm) => _origBuf?.AddSamples(pcm, 0, pcm.Length);

        /// <summary>Fills dst[0..count) with this channel's audio (tap + optional mic), silence-padded.</summary>
        public void Read(float[] dst, int count)
        {
            _src.Read(dst, 0, count);
            if (_origSrc is null) return;

            if (_os.Length < count) _os = new float[count];
            _origSrc.Read(_os, 0, count);
            for (int i = 0; i < count; i++)
                dst[i] += _os[i];
        }
    }
}
