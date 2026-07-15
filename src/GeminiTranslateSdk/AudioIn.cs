using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateSdk;

/// <summary>
/// Captures a mic (or a render endpoint via loopback) and emits mono PCM16 at the device's
/// NATIVE sample rate in ~100 ms chunks. No resampling: Google's own reference bridge sends
/// 48 kHz straight to the translate model, and full-bandwidth input gives it noticeably more
/// natural segmentation/prosody than 16 kHz (which mutes everything above 8 kHz). The audio is
/// passed through untouched, and every chunk is forwarded — no local silence gate. Per the Live
/// API docs and reference clients, that's by design: "send audio chunks as they become
/// available without waiting for silence" and "the server handles VAD automatically — you don't
/// need local client-side detection"; the only client-side signal expected is audioStreamEnd,
/// and only when the stream itself actually pauses (mic muted), not a guess based on measured
/// silence duration (see Direction's Muted setter). An earlier version of this class tried to
/// own turn/pause detection locally via RMS + a timeout and it fought the server's own
/// automaticActivityDetection.silenceDurationMs — right idea, wrong layer.
/// </summary>
public sealed class AudioIn : IDisposable
{
    private readonly int _chunkBytes; // 100 ms @ native rate, mono PCM16

    private readonly string _tag;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _outMono;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Sample rate of the emitted chunks (the device's native rate).</summary>
    public int SampleRate { get; }

    /// <summary>Native-rate mono PCM16 chunk, exactly as captured — fired for EVERY chunk while not muted.</summary>
    public event Action<byte[]>? ChunkAvailable;
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
        SampleRate = sp.WaveFormat.SampleRate;
        _chunkBytes = SampleRate / 10 * 2; // 100 ms of mono PCM16
        _outMono = new SampleToWaveProvider16(sp);

        Log.Write(_tag, $"captura em '{device.FriendlyName}' (loopback={loopback}, " +
                        $"{_capture.WaveFormat.SampleRate} Hz {_capture.WaveFormat.Channels} ch), " +
                        $"enviando {SampleRate} Hz mono sem resample.");
    }

    public void Start()
    {
        _capture.StartRecording();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var temp = new byte[_chunkBytes];
        var acc = new byte[_chunkBytes * 4];
        int accLen = 0;

        while (!ct.IsCancellationRequested)
        {
            int read = _outMono.Read(temp, 0, temp.Length);
            if (read <= 0)
            {
                try { await Task.Delay(10, ct); } catch { break; }
                continue;
            }

            if (accLen + read > acc.Length) accLen = 0;
            Buffer.BlockCopy(temp, 0, acc, accLen, read);
            accLen += read;

            int offset = 0;
            while (accLen - offset >= _chunkBytes)
            {
                Level?.Invoke(Rms(acc, offset, _chunkBytes)); // VU meter only — doesn't gate sending

                if (!Muted)
                {
                    var chunk = new byte[_chunkBytes];
                    Buffer.BlockCopy(acc, offset, chunk, 0, _chunkBytes);
                    ChunkAvailable?.Invoke(chunk);
                }
                offset += _chunkBytes;
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
