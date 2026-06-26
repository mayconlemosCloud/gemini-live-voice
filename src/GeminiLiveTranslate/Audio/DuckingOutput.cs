using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Plays a mix of the ORIGINAL audio (passthrough) and the TRANSLATION audio, ducking
/// (lowering) the original whenever the translation is speaking — the "Google Meet" effect.
/// Because the app itself renders the original here, capture and playback live on different
/// endpoints, which structurally prevents the capture-its-own-output feedback loop.
/// </summary>
public sealed class DuckingOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _origBuf;   // 16 kHz mono PCM16 (passthrough of the source)
    private readonly BufferedWaveProvider _transBuf;  // 24 kHz mono PCM16 (translation from Gemini)
    private readonly DuckMixProvider _mix;
    private bool _disposed;
    private long _transEnq, _origEnq;

    public string Tag { get; set; } = "Player";

    /// <summary>Drop translation backlog above this so what you hear stays near real time.</summary>
    public int MaxLatencyMs { get; set; } = 1200;

    /// <summary>When true, the original audio is mixed in (ducked). When false, only the translation plays.</summary>
    public bool PassthroughOriginal { get => _mix.Passthrough; set => _mix.Passthrough = value; }

    /// <summary>Original volume (0..1) while the translation is speaking.</summary>
    public float DuckLevel { get => _mix.DuckLevel; set => _mix.DuckLevel = value; }

    public bool TranslationActive => _transBuf.BufferedDuration.TotalMilliseconds > 60;

    public DuckingOutput(MMDevice device)
    {
        _device = device;
        _origBuf = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(10), DiscardOnBufferOverflow = true };
        _transBuf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        { ReadFully = true, BufferDuration = TimeSpan.FromSeconds(60), DiscardOnBufferOverflow = true };

        var mix = device.AudioClient.MixFormat;
        ISampleProvider orig = ToMixFormat(_origBuf.ToSampleProvider(), mix);
        ISampleProvider trans = ToMixFormat(_transBuf.ToSampleProvider(), mix);
        _mix = new DuckMixProvider(orig, trans, _transBuf);

        IWaveProvider final = new SampleToWaveProvider(_mix);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.Init(final);
        Log.Info(Tag, $"Saída com ducking em '{device.FriendlyName}'. Mix {mix.SampleRate} Hz {mix.Channels} ch.");
    }

    private static ISampleProvider ToMixFormat(ISampleProvider sp, WaveFormat mix)
    {
        if (sp.WaveFormat.SampleRate != mix.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
        if (mix.Channels == 2)
            sp = new MonoToStereoSampleProvider(sp);
        else if (mix.Channels > 2)
            sp = new MonoToMultiSampleProvider(sp, mix.Channels);
        return sp;
    }

    public void Start()
    {
        try { _output.Play(); Log.Info(Tag, "Playback iniciado."); }
        catch (Exception ex) { Log.Error(Tag, "Falha ao iniciar playback", ex); throw; }
    }

    /// <summary>Feed the original (untranslated) audio for passthrough: 16 kHz mono PCM16.</summary>
    public void EnqueueOriginal(byte[] pcm16k)
    {
        if (_origBuf.BufferedDuration.TotalMilliseconds > 800)
            _origBuf.ClearBuffer(); // keep passthrough low-latency
        _origBuf.AddSamples(pcm16k, 0, pcm16k.Length);
        if (Interlocked.Increment(ref _origEnq) == 1)
            Log.Info(Tag, "Primeiro áudio original (passthrough) na fila.");
    }

    /// <summary>Feed translated audio from Gemini: 24 kHz mono PCM16.</summary>
    public void EnqueueTranslation(byte[] pcm24k)
    {
        if (_transBuf.BufferedDuration.TotalMilliseconds > MaxLatencyMs)
        {
            Log.Warn(Tag, $"Atraso de tradução > {MaxLatencyMs} ms — descartando backlog para manter tempo real.");
            _transBuf.ClearBuffer();
        }
        _transBuf.AddSamples(pcm24k, 0, pcm24k.Length);
        long n = Interlocked.Increment(ref _transEnq);
        if (n == 1) Log.Info(Tag, "Primeira tradução na fila de reprodução.");
        if (n % 50 == 0) Log.Debug(Tag, $"Tradução: {n} chunks, buffer {_transBuf.BufferedDuration.TotalMilliseconds:F0} ms.");
    }

    public void ClearTranslation() => _transBuf.ClearBuffer();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}

/// <summary>Mixes original + translation, ramping the original down while the translation speaks.</summary>
internal sealed class DuckMixProvider : ISampleProvider
{
    private readonly ISampleProvider _orig;
    private readonly ISampleProvider _trans;
    private readonly BufferedWaveProvider _transBuf;
    private float[] _t = Array.Empty<float>();
    private float[] _o = Array.Empty<float>();
    private float _gain = 1f;

    public bool Passthrough { get; set; } = true;
    public float DuckLevel { get; set; } = 0.18f;
    public WaveFormat WaveFormat => _orig.WaveFormat;

    public DuckMixProvider(ISampleProvider orig, ISampleProvider trans, BufferedWaveProvider transBuf)
    {
        _orig = orig;
        _trans = trans;
        _transBuf = transBuf;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_t.Length < count) { _t = new float[count]; _o = new float[count]; }
        Array.Clear(_t, 0, count);
        Array.Clear(_o, 0, count);
        _trans.Read(_t, 0, count);
        if (Passthrough) _orig.Read(_o, 0, count);

        bool active = _transBuf.BufferedDuration.TotalMilliseconds > 60;
        float target = active ? DuckLevel : 1f;
        int channels = Math.Max(1, WaveFormat.Channels);
        float step = 1f / (WaveFormat.SampleRate * 0.03f); // ~30 ms ramp, no clicks

        for (int i = 0; i < count; i++)
        {
            if (i % channels == 0)
            {
                if (_gain < target) _gain = Math.Min(target, _gain + step);
                else if (_gain > target) _gain = Math.Max(target, _gain - step);
            }
            float s = (Passthrough ? _o[i] * _gain : 0f) + _t[i];
            buffer[offset + i] = s > 1f ? 1f : s < -1f ? -1f : s;
        }
        return count;
    }
}
