using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateV2;

/// <summary>
/// Entrada alternative to ProcessCapture — the GeminiTranslateLite approach: capture a render
/// endpoint via WASAPI loopback. Point the call app's output at a dedicated virtual cable and
/// select the cable here; the meeting then reaches your ears only through this app's mix
/// (translation + original underneath), never doubled. Native-rate mono PCM16 in ~100 ms
/// chunks, no local silence gate — same contract as the other IAudioSources, no quality
/// difference: both this and ProcessCapture are bit-clean digital taps after the mixer.
/// The trade-off is CLEANLINESS, not fidelity: loopback hears EVERYTHING routed to the
/// device, so it is only as clean as the routing — on a dedicated cable it is equivalent
/// (true digital silence between sentences); on your everyday speakers every notification
/// ding also goes to the model.
/// </summary>
public sealed class LoopbackCapture : IAudioSource
{
    private readonly int _chunkBytes;
    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _outMono;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int SampleRate { get; }
    public event Action<byte[]>? ChunkAvailable;
    public event Action<float>? Level;
    public bool Muted { get; set; }

    public LoopbackCapture(MMDevice device)
    {
        _capture = new WasapiLoopbackCapture(device);
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
        // Loopback audio is a produced stereo mix, so averaging the channels is correct here
        // (unlike a mic array — see MicCapture's FirstChannel).
        if (sp.WaveFormat.Channels > 1) sp = new AnyToMono(sp);
        SampleRate = sp.WaveFormat.SampleRate;
        _chunkBytes = SampleRate / 10 * 2; // 100 ms mono PCM16
        _outMono = new SampleToWaveProvider16(sp);

        Log.Write("Loopback", $"captura em '{device.FriendlyName}' ({_capture.WaveFormat.SampleRate} Hz " +
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
        catch (Exception ex) { Log.Write("Loopback", $"loop de captura morreu: {ex}"); }
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
