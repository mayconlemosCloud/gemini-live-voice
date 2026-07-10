using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateLite;

/// <summary>
/// Plays the TRANSLATION (24 kHz mono PCM16 from Gemini) at full volume with the ORIGINAL
/// voice (16 kHz mono PCM16 from the capture) mixed underneath at a fixed low volume.
/// No dynamic ducking, no trimming, no jitter logic — the model's realtime stream already
/// carries its own pacing.
/// </summary>
public sealed class AudioOut : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _transBuf;
    private readonly BufferedWaveProvider _origBuf;
    private readonly VolumeSampleProvider _origVolume;
    private bool _disposed;

    /// <summary>Volume 0..1 of the original voice under the translation; adjustable live.</summary>
    public float OriginalVolume
    {
        get => _origVolume.Volume;
        set => _origVolume.Volume = Math.Clamp(value, 0f, 1f);
    }

    public AudioOut(MMDevice device, float originalVolume, string tag)
    {
        _device = device;
        _transBuf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(60), DiscardOnBufferOverflow = true };
        _origBuf = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(5), DiscardOnBufferOverflow = true };

        var mixFormat = device.AudioClient.MixFormat;
        // 150 ms pre-roll on both lanes: chunks arrive every ~100 ms (capture) / in network
        // bursts (translation); with a zero cushion any timing wobble empties the buffer and
        // punches an audible hole mid-word. The pre-roll delays each lane slightly and never
        // alters the content.
        ISampleProvider trans = ToDevice(
            new Preroll(_transBuf.ToSampleProvider(), _transBuf, 150), mixFormat);
        _origVolume = new VolumeSampleProvider(ToDevice(
            new Preroll(_origBuf.ToSampleProvider(), _origBuf, 150), mixFormat))
        { Volume = Math.Clamp(originalVolume, 0f, 1f) };

        var mix = new MixingSampleProvider(new[] { trans, _origVolume }) { ReadFully = true };
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.Init(new SampleToWaveProvider(mix));
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

    public void EnqueueOriginal(byte[] pcm16k)
    {
        // Keep the passthrough near-live: if it ever lags (device hiccup), reset instead of drifting.
        if (_origBuf.BufferedDuration.TotalMilliseconds > 1000) _origBuf.ClearBuffer();
        _origBuf.AddSamples(pcm16k, 0, pcm16k.Length);
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
/// Outputs silence until the backing buffer holds <c>prerollMs</c>, then plays until it drains;
/// re-arms on drain. Content is only delayed, never dropped or modified.
/// </summary>
internal sealed class Preroll : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly NAudio.Wave.BufferedWaveProvider _buf;
    private readonly int _prerollMs;
    private bool _playing;

    public Preroll(ISampleProvider src, NAudio.Wave.BufferedWaveProvider buf, int prerollMs)
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
        int n = _src.Read(buffer, offset, count); // src is ReadFully → always fills
        if (ms <= 1.0) _playing = false;          // drained → re-prime for the next burst
        return n;
    }
}

/// <summary>Copies a mono source into every channel of an N-channel stream.</summary>
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
