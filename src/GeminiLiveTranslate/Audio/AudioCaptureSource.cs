using GeminiLiveTranslate.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiLiveTranslate.Audio;

/// <summary>
/// Captures from a microphone (or a render endpoint in loopback mode) and emits
/// 16 kHz / mono / 16-bit PCM in ~100 ms chunks, the exact format the Gemini Live
/// translate model expects on input.
/// </summary>
public sealed class AudioCaptureSource : IDisposable
{
    // 100 ms @ 16 kHz, mono, 16-bit = 16000 * 0.1 * 2 bytes.
    private const int ChunkBytes = 3200;

    private readonly MMDevice _device;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _out16k;

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _disposed;

    /// <summary>Short label used as the log category.</summary>
    public string Tag { get; set; } = "Capture";

    /// <summary>Fired with a 16 kHz mono PCM16 chunk ready to send to Gemini.</summary>
    public event Action<byte[]>? ChunkAvailable;

    /// <summary>Fired roughly every 100 ms with the RMS level (0..1) for the VU meter.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>When true, chunks are dropped (push-to-mute).</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// When true (default) the full audio stream is sent uninterrupted so the model
    /// keeps prosody/voice context. When false, a client-side silence gate is applied
    /// (lower API usage, but worse intonation and possible voice resets on pauses).
    /// </summary>
    public bool ContinuousMode { get; set; } = true;

    /// <summary>RMS threshold below which audio is treated as silence (only used when ContinuousMode is false).</summary>
    public float Gate { get; set; } = 0.006f;

    public AudioCaptureSource(MMDevice device, bool loopback)
    {
        _device = device;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true
        };
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
                _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new ToMonoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != 16000)
            sp = new WdlResamplingSampleProvider(sp, 16000);
        _out16k = new SampleToWaveProvider16(sp);

        var f = _capture.WaveFormat;
        Log.Info(Tag, $"Captura criada em '{device.FriendlyName}' (loopback={loopback}). " +
                      $"Formato do dispositivo: {f.SampleRate} Hz, {f.Channels} ch, {f.BitsPerSample} bits, {f.Encoding}. " +
                      $"→ convertendo para 16000 Hz mono PCM16.");
    }

    public void Start()
    {
        try
        {
            _capture.StartRecording();
            Log.Info(Tag, "StartRecording OK.");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Falha em StartRecording", ex);
            throw;
        }
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var temp = new byte[ChunkBytes];
        var acc = new byte[ChunkBytes * 4];
        int accLen = 0;
        int hangover = 0;

        long sent = 0, silenced = 0, firstLogged = 0;
        float peak = 0;
        var statsClock = System.Diagnostics.Stopwatch.StartNew();
        var emptyLogClock = System.Diagnostics.Stopwatch.StartNew();
        Log.Info(Tag, "Loop de captura iniciado.");

        while (!ct.IsCancellationRequested)
        {
            int read = _out16k.Read(temp, 0, temp.Length);
            if (read <= 0)
            {
                // Throttle to at most one line per 4 s so the log stays readable.
                if (emptyLogClock.ElapsedMilliseconds >= 4000)
                {
                    Log.Debug(Tag, "Sem áudio neste dispositivo no momento (silêncio / nada tocando).");
                    emptyLogClock.Restart();
                }
                try { await Task.Delay(10, ct); } catch { break; }
                continue;
            }

            if (accLen + read > acc.Length)
                accLen = 0; // safety reset; should never happen with paced realtime input
            Buffer.BlockCopy(temp, 0, acc, accLen, read);
            accLen += read;

            int offset = 0;
            while (accLen - offset >= ChunkBytes)
            {
                float rms = Rms(acc, offset, ChunkBytes);
                LevelChanged?.Invoke(rms);
                if (rms > peak) peak = rms;

                bool voiced = rms >= Gate;
                if (voiced) hangover = 10; // ~1 s tail so word endings aren't clipped
                else if (hangover > 0) hangover--;

                // Continuous: send everything (best prosody). Otherwise apply the silence gate.
                bool shouldSend = !Muted && (ContinuousMode || voiced || hangover > 0);
                if (shouldSend)
                {
                    var chunk = new byte[ChunkBytes];
                    Buffer.BlockCopy(acc, offset, chunk, 0, ChunkBytes);
                    ChunkAvailable?.Invoke(chunk);
                    sent++;
                    if (firstLogged == 0) { firstLogged = 1; Log.Info(Tag, $"Primeiro chunk enviado (modo {(ContinuousMode ? "contínuo" : "gate")}, rms={rms:F3})."); }
                }
                else silenced++;
                offset += ChunkBytes;

                if (statsClock.ElapsedMilliseconds >= 5000)
                {
                    Log.Debug(Tag, $"Stats 5s: enviados={sent} silêncio={silenced} pico_rms={peak:F3} gate={Gate:F3} mudo={Muted}");
                    sent = silenced = 0; peak = 0; statsClock.Restart();
                }
            }

            int remaining = accLen - offset;
            if (remaining > 0)
                Buffer.BlockCopy(acc, offset, acc, 0, remaining);
            accLen = remaining;
        }
    }

    private static float Rms(byte[] data, int offset, int count)
    {
        int samples = count / 2;
        if (samples == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(data[offset + i * 2] | (data[offset + i * 2 + 1] << 8));
            float f = s / 32768f;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / samples);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Info(Tag, "Encerrando captura.");
        try { _cts?.Cancel(); } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _pump?.Wait(500); } catch { }
        try { _capture.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
