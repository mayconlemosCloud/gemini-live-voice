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
    private volatile Action<float[], int, int>? _renderTap;
    private bool _disposed;

    /// <summary>Translated audio waiting to be played — the live delay the listener hears.</summary>
    public TimeSpan TranslationQueue => _transBuf.BufferedDuration;

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

        // 20 ms preroll + 30 ms WASAPI buffer (were 150/100, depois 40/50): share-tab.html provou
        // que preroll zero já é fluido, então sobra só uma guarda mínima contra engasgo.
        // A tradução toca em 1× e só. O CatchUp que ficava aqui consumia a fila a 1,15× por
        // interpolação linear quando ela crescia — o que SOBE O PITCH ~15% enquanto está engatado.
        // Isso é destruir exatamente a entonação que o modelo acabou de copiar da sua voz para
        // economizar alguns décimos de segundo. O AI Studio toca cada bloco na hora que chega, em
        // 1×, e é esse o comportamento agora. Se a fila crescer, ela cresce: é atraso honesto.
        var mixFormat = device.AudioClient.MixFormat;
        ISampleProvider trans = ToDevice(new Preroll(_transBuf.ToSampleProvider(), _transBuf, 20), mixFormat);
        _origVolume = new VolumeSampleProvider(ToDevice(new Preroll(_origBuf.ToSampleProvider(), _origBuf, 20), mixFormat))
        { Volume = Math.Clamp(originalVolume, 0f, 1f) };

        var mix = new MixingSampleProvider(new[] { trans, _origVolume }) { ReadFully = true };
        MixFormat = mix.WaveFormat;
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
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
