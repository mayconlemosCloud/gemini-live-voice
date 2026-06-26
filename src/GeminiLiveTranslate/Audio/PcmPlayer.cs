using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Plays the 24 kHz / mono / 16-bit PCM stream returned by Gemini to a chosen render
/// endpoint, resampling/up-mixing to the device mix format so any speaker or virtual
/// cable accepts it.
/// </summary>
public sealed class PcmPlayer : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _buffer;
    private bool _disposed;
    private long _enqueued;

    /// <summary>Short label used as the log category.</summary>
    public string Tag { get; set; } = "Player";

    public PcmPlayer(MMDevice device)
    {
        _device = device;
        _buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(60),
            DiscardOnBufferOverflow = true
        };

        var mix = device.AudioClient.MixFormat;
        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.SampleRate != mix.SampleRate)
            sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
        if (mix.Channels == 2)
            sp = new MonoToStereoSampleProvider(sp);
        else if (mix.Channels > 2)
            sp = new MonoToMultiSampleProvider(sp, mix.Channels);

        IWaveProvider final = new SampleToWaveProvider(sp); // 32-bit IEEE float, matches mix format
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.Init(final);
        Log.Info(Tag, $"Player criado em '{device.FriendlyName}'. " +
                      $"Mix do dispositivo: {mix.SampleRate} Hz, {mix.Channels} ch, {mix.Encoding}. " +
                      $"Entrada 24000 Hz mono → resample/upmix para o mix.");
    }

    public void Start()
    {
        try { _output.Play(); Log.Info(Tag, "Playback iniciado."); }
        catch (Exception ex) { Log.Error(Tag, "Falha ao iniciar playback", ex); throw; }
    }

    /// <summary>
    /// If the playback backlog grows past this, the accumulated delay is dropped so the
    /// translation you hear stays close to real time instead of lagging and overlapping.
    /// </summary>
    public int MaxLatencyMs { get; set; } = 1200;

    public void Enqueue(byte[] pcm24k)
    {
        // Bound latency: never let the buffer pile up (matches the realtime-paced
        // playback of the reference LiveKit implementation).
        if (_buffer.BufferedDuration.TotalMilliseconds > MaxLatencyMs)
        {
            Log.Warn(Tag, $"Atraso de reprodução > {MaxLatencyMs} ms — descartando backlog para manter tempo real.");
            _buffer.ClearBuffer();
        }

        _buffer.AddSamples(pcm24k, 0, pcm24k.Length);
        long n = Interlocked.Increment(ref _enqueued);
        if (n == 1) Log.Info(Tag, "Primeiro áudio na fila de reprodução.");
        if (n % 50 == 0) Log.Debug(Tag, $"Reprodução: {n} chunks na fila, buffer atual {_buffer.BufferedDuration.TotalMilliseconds:F0} ms.");
    }

    public void Clear() => _buffer.ClearBuffer();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
