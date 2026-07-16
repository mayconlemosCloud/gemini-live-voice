using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateV2;

/// <summary>
/// Plays the TRANSLATION (24 kHz mono PCM16 from Gemini) at full volume with the ORIGINAL
/// voice (native-rate mono PCM16 from the capture) mixed underneath at a fixed low volume.
/// Latency numbers (preroll, WASAPI buffer) are tuned to match share-tab.html, which plays
/// each chunk the instant it arrives and sounds fine — so the guards here are minimal.
/// </summary>
public sealed class AudioOut : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _transBuf;
    private readonly BufferedWaveProvider _origBuf;
    private readonly VolumeSampleProvider _origVolume;
    private readonly CatchUp _catchUp;
    private volatile Action<float[], int, int>? _renderTap;
    private bool _disposed;

    /// <summary>Translated audio waiting to be played — the live delay the listener hears.</summary>
    public TimeSpan TranslationQueue => _transBuf.BufferedDuration;

    /// <summary>True while the queue got long and the translation is playing at 1.1×.</summary>
    public bool CatchingUp => _catchUp.Fast;

    /// <summary>Format of the final mixed stream (IEEE float, device mix rate/channels).</summary>
    public WaveFormat MixFormat { get; }

    /// <summary>
    /// Called from the render thread with every block of mixed samples actually played —
    /// exactly what the listener hears (translation + original underneath). Used by
    /// ConversationRecorder; null means no tap.
    /// </summary>
    public Action<float[], int, int>? RenderTap
    {
        get => _renderTap;
        set => _renderTap = value;
    }

    public float OriginalVolume
    {
        get => _origVolume.Volume;
        set => _origVolume.Volume = Math.Clamp(value, 0f, 1f);
    }

    public AudioOut(MMDevice device, int originalRate, float originalVolume, string tag)
    {
        _device = device;
        _transBuf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(60), DiscardOnBufferOverflow = true };
        _origBuf = new BufferedWaveProvider(new WaveFormat(originalRate, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(5), DiscardOnBufferOverflow = true };

        // 40 ms preroll + 50 ms WASAPI buffer (were 150/100): share-tab.html proved zero preroll
        // is already smooth, so keep only a small guard against drip-feed stutter.
        var mixFormat = device.AudioClient.MixFormat;
        _catchUp = new CatchUp(_transBuf.ToSampleProvider(), _transBuf, tag);
        ISampleProvider trans = ToDevice(new Preroll(_catchUp, _transBuf, 40), mixFormat);
        _origVolume = new VolumeSampleProvider(ToDevice(new Preroll(_origBuf.ToSampleProvider(), _origBuf, 40), mixFormat))
        { Volume = Math.Clamp(originalVolume, 0f, 1f) };

        var mix = new MixingSampleProvider(new[] { trans, _origVolume }) { ReadFully = true };
        MixFormat = mix.WaveFormat;
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
        _output.Init(new SampleToWaveProvider(new TapSampleProvider(mix, () => _renderTap)));
        Log.Write(tag, $"saída em '{device.FriendlyName}' ({mixFormat.SampleRate} Hz {mixFormat.Channels} ch), original a {originalVolume:P0}.");
    }

    private static ISampleProvider ToDevice(ISampleProvider sp, WaveFormat mix)
    {
        if (sp.WaveFormat.SampleRate != mix.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
        if (mix.Channels == 2)
            sp = new MonoToStereoSampleProvider(sp);
        else if (mix.Channels > 2)
            sp = new MonoToChannels(sp, mix.Channels);
        return sp;
    }

    public void Start() => _output.Play();
    public void EnqueueTranslation(byte[] pcm24k) => _transBuf.AddSamples(pcm24k, 0, pcm24k.Length);

    public void EnqueueOriginal(byte[] pcm)
    {
        if (_origBuf.BufferedDuration.TotalMilliseconds > 1000) _origBuf.ClearBuffer();
        _origBuf.AddSamples(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}

/// <summary>
/// Anti-acúmulo: when continuous speech makes translated audio queue up faster than it plays
/// out (the delay that grows over a long session), consume the source at 1.1× until the queue
/// drains, then drop back to 1×. Plain linear interpolation — pitch rises ~10% while engaged,
/// the same trade-off Meet makes for live catch-up. Hysteresis (engage at 3 s, release at
/// 0.75 s) keeps it from flapping on ordinary bursts.
/// </summary>
internal sealed class CatchUp : ISampleProvider
{
    private const float FastRate = 1.10f;
    private const double EngageMs = 3000;
    private const double ReleaseMs = 750;

    private readonly ISampleProvider _src; // mono; ReadFully source, so reads always fill
    private readonly BufferedWaveProvider _queue;
    private readonly string _tag;

    private volatile bool _fast;
    private bool _primed;
    private double _frac;
    private float _p, _n;
    private float[] _in = Array.Empty<float>();
    private int _inLen, _inPos;

    public bool Fast => _fast;

    public CatchUp(ISampleProvider src, BufferedWaveProvider queue, string tag)
    {
        _src = src;
        _queue = queue;
        _tag = tag;
    }

    public WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        double ms = _queue.BufferedDuration.TotalMilliseconds;
        if (_fast && ms <= ReleaseMs)
        {
            _fast = false;
            _primed = false; // drops ≤2 samples of interpolation state — inaudible
            Log.Write(_tag, $"catch-up desligado (fila {ms:0} ms).");
        }
        else if (!_fast && ms >= EngageMs)
        {
            _fast = true;
            Log.Write(_tag, $"catch-up ligado: tradução a {FastRate:0.00}× (fila {ms:0} ms).");
        }

        if (!_fast)
        {
            // Serve whatever was already pulled from the queue before returning to pass-through.
            int served = 0;
            while (served < count && _inPos < _inLen)
                buffer[offset + served++] = _in[_inPos++];
            if (served < count)
                served += _src.Read(buffer, offset + served, count - served);
            return served;
        }

        if (!_primed)
        {
            _p = NextSource();
            _n = NextSource();
            _frac = 0;
            _primed = true;
        }
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = _p + (_n - _p) * (float)_frac;
            _frac += FastRate;
            while (_frac >= 1.0)
            {
                _p = _n;
                _n = NextSource();
                _frac -= 1.0;
            }
        }
        return count;
    }

    private float NextSource()
    {
        if (_inPos >= _inLen)
        {
            if (_in.Length == 0) _in = new float[1024];
            _inLen = _src.Read(_in, 0, _in.Length);
            _inPos = 0;
            if (_inLen <= 0) return 0f; // ReadFully source shouldn't hit this; safety only
        }
        return _in[_inPos++];
    }
}

/// <summary>Passes samples through untouched, handing each played block to the current tap.</summary>
internal sealed class TapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly Func<Action<float[], int, int>?> _tap;

    public TapSampleProvider(ISampleProvider src, Func<Action<float[], int, int>?> tap)
    {
        _src = src;
        _tap = tap;
    }

    public WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        if (n > 0) _tap()?.Invoke(buffer, offset, n);
        return n;
    }
}

internal sealed class Preroll : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly BufferedWaveProvider _buf;
    private readonly int _prerollMs;
    private bool _playing;

    public Preroll(ISampleProvider src, BufferedWaveProvider buf, int prerollMs)
    {
        _src = src;
        _buf = buf;
        _prerollMs = prerollMs;
    }

    public WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        double ms = _buf.BufferedDuration.TotalMilliseconds;
        if (!_playing && ms >= _prerollMs) _playing = true;
        if (!_playing)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        int n = _src.Read(buffer, offset, count);
        if (ms <= 1.0) _playing = false;
        return n;
    }
}

internal sealed class MonoToChannels : ISampleProvider
{
    private readonly ISampleProvider _src;
    private float[] _mono = Array.Empty<float>();

    public MonoToChannels(ISampleProvider src, int channels)
    {
        _src = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int ch = WaveFormat.Channels;
        int frames = count / ch;
        if (_mono.Length < frames) _mono = new float[frames];
        int got = _src.Read(_mono, 0, frames);
        for (int f = 0; f < got; f++)
            for (int c = 0; c < ch; c++)
                buffer[offset + f * ch + c] = _mono[f];
        return got * ch;
    }
}
