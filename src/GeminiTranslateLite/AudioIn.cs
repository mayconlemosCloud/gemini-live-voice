using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateLite;

/// <summary>
/// Captures a mic (or a render endpoint via loopback) and emits 16 kHz mono PCM16 in ~100 ms
/// chunks — the input format of the translate model. The audio is passed through untouched;
/// the only guard is a silence stop: after 3 s without voice nothing is sent until voice
/// returns, because the model provably hallucinates repeated phrases when fed continuous
/// silence (see logs from 2026-07-06).
/// </summary>
public sealed class AudioIn : IDisposable
{
    private const int ChunkBytes = 3200; // 100 ms @ 16 kHz mono PCM16
    private const float VoiceRms = 0.005f;
    private const int SilenceStopMs = 3000;

    private readonly string _tag;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _out16k;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// 16 kHz mono PCM16 chunk, exactly as captured — fired for EVERY chunk while not muted.
    /// The bool says whether the chunk should be SENT to the model (false after 3 s without
    /// voice — the anti-hallucination stop). The original-voice passthrough must always flow,
    /// so gating is the consumer's choice, applied to the send only: coupling the passthrough
    /// to the gate made the original voice drop in and out on soft speech.
    /// </summary>
    public event Action<byte[], bool>? ChunkAvailable;
    /// <summary>RMS 0..1, ~10×/s, for a VU meter.</summary>
    public event Action<float>? Level;

    public bool Muted { get; set; }

    public AudioIn(MMDevice device, bool loopback, string tag)
    {
        _tag = tag;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(10),
            DiscardOnBufferOverflow = true
        };
        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };

        ISampleProvider sp = _buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
        {
            // Mic: take channel 0 only. Averaging the channels of a mic ARRAY combines capsules
            // with different phases (comb filtering) and thins the voice — the "app is limiting
            // my mic" effect. Loopback (meeting) audio is a produced stereo mix, so averaging is
            // correct there.
            sp = loopback ? new ToMono(sp) : new FirstChannel(sp);
        }
        if (sp.WaveFormat.SampleRate != 16000) sp = new WdlResamplingSampleProvider(sp, 16000);
        _out16k = new SampleToWaveProvider16(sp);

        Log.Write(_tag, $"captura em '{device.FriendlyName}' (loopback={loopback}, " +
                        $"{_capture.WaveFormat.SampleRate} Hz {_capture.WaveFormat.Channels} ch).");
    }

    public void Start()
    {
        _capture.StartRecording();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var temp = new byte[ChunkBytes];
        var acc = new byte[ChunkBytes * 4];
        int accLen = 0;
        int silentMs = SilenceStopMs; // start silent: nothing is sent until there is voice

        while (!ct.IsCancellationRequested)
        {
            int read = _out16k.Read(temp, 0, temp.Length);
            if (read <= 0)
            {
                try { await Task.Delay(10, ct); } catch { break; }
                continue;
            }

            if (accLen + read > acc.Length) accLen = 0;
            Buffer.BlockCopy(temp, 0, acc, accLen, read);
            accLen += read;

            int offset = 0;
            while (accLen - offset >= ChunkBytes)
            {
                float rms = Rms(acc, offset, ChunkBytes);
                Level?.Invoke(rms);
                silentMs = rms >= VoiceRms ? 0 : silentMs + 100;

                if (!Muted)
                {
                    var chunk = new byte[ChunkBytes];
                    Buffer.BlockCopy(acc, offset, chunk, 0, ChunkBytes);
                    ChunkAvailable?.Invoke(chunk, silentMs <= SilenceStopMs);
                }
                offset += ChunkBytes;
            }

            int remaining = accLen - offset;
            if (remaining > 0) Buffer.BlockCopy(acc, offset, acc, 0, remaining);
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
        try { _cts?.Cancel(); } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}

/// <summary>Extracts channel 0 of a multi-channel stream (no mixing, no phase interaction).</summary>
internal sealed class FirstChannel : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _channels;
    private float[] _buf = Array.Empty<float>();

    public FirstChannel(ISampleProvider src)
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
            buffer[offset + f] = _buf[f * _channels];
        return frames;
    }
}

/// <summary>Averages any number of channels down to mono.</summary>
internal sealed class ToMono : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _channels;
    private float[] _buf = Array.Empty<float>();

    public ToMono(ISampleProvider src)
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
