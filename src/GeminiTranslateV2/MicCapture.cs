using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateV2;

/// <summary>
/// Real microphone capture at its native rate, mono PCM16, ~100 ms chunks. No local silence
/// gate — every chunk is forwarded; the server's automaticActivityDetection decides turns
/// (see LiveClient). Turn the mic's own noise suppression / echo cancellation on in Windows
/// Settings (System → Sound → your microphone → "Enhance audio") — that's what actually cleans
/// the signal, not anything this class does.
/// </summary>
public sealed class MicCapture : IAudioSource
{
    private readonly int _chunkBytes;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _outMono;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int SampleRate { get; }
    public event Action<byte[]>? ChunkAvailable;
    public event Action<float>? Level;
    public bool Muted { get; set; }

    public MicCapture(MMDevice device)
    {
        _capture = new WasapiCapture(device);
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
        if (sp.WaveFormat.Channels > 1) sp = new FirstChannel(sp); // channel 0 only — no comb filtering across capsules
        SampleRate = sp.WaveFormat.SampleRate;
        _chunkBytes = SampleRate / 10 * 2; // 100 ms mono PCM16
        _outMono = new SampleToWaveProvider16(sp);

        Log.Write("Mic", $"captura em '{device.FriendlyName}' ({_capture.WaveFormat.SampleRate} Hz " +
                         $"{_capture.WaveFormat.Channels} ch), enviando {SampleRate} Hz mono sem resample.");
    }

    public Task StartAsync()
    {
        _capture.StartRecording();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try { await PumpCoreAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Mic", $"loop de captura morreu: {ex}"); }
    }

    private async Task PumpCoreAsync(CancellationToken ct)
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
                Level?.Invoke(Rms(acc, offset, _chunkBytes));
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
