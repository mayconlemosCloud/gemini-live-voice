using System.Runtime.InteropServices;

namespace GeminiTranslateV2;

/// <summary>
/// Captures ONLY the audio rendered by one process (e.g. Teams.exe, chrome.exe, WhatsApp.exe)
/// via Windows Process Loopback — not the whole system like a virtual audio cable would. This
/// is what makes silence between the other person's sentences read as true digital silence to
/// Gemini's VAD, instead of the mixed-system noise floor that caused the "won't stop talking"
/// bug in earlier versions. No local silence gate here either — same reasoning as MicCapture.
///
/// Event-driven, not polled: the first working version used a GetNextPacketSize + Task.Delay(10)
/// polling loop on a plain thread-pool thread, and a side-by-side comparison against
/// share-tab.html (real browser tab capture) showed it was noticeably less fluid. Polling with
/// sleeps on a generic thread-pool thread has no timing guarantee — under any contention
/// (WebSocket I/O, WAV writes, UI) the 10ms poll can slip, producing exactly the kind of
/// jitter/choppiness a proper low-latency capture avoids. WASAPI supports an event-driven mode
/// (AUDCLNT_STREAMFLAGS_EVENTCALLBACK) precisely for this — the OS signals a handle the instant
/// a buffer is ready, no sleeping/guessing — which is what NAudio's own WasapiCapture uses
/// internally, and what this class now uses too, on a priority-boosted dedicated thread.
/// </summary>
public sealed class ProcessCapture : IAudioSource
{
    private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private readonly uint _processId;
    private ProcessLoopback.IAudioClient? _client;
    private ProcessLoopback.IAudioCaptureClient? _captureClient;
    private AutoResetEvent? _bufferEvent;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public int SampleRate => ProcessLoopback.SampleRate; // fixed by the API: 48000 Hz
    public event Action<byte[]>? ChunkAvailable;
    public event Action<float>? Level;
    public bool Muted { get; set; }

    public ProcessCapture(uint processId) => _processId = processId;

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Activation AND the whole capture loop run on the SAME dedicated thread (never the WPF
        // Dispatcher/STA thread — splitting those caused a real E_NOINTERFACE bug here earlier).
        var thread = new Thread(() => RunOnDedicatedThread(ready))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "ProcessCapture"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        return ready.Task;
    }

    private void RunOnDedicatedThread(TaskCompletionSource ready)
    {
        try
        {
            // OpenAsync itself only awaits a COM callback (no real async work), so blocking on
            // it here — instead of a nested Task.Run — keeps everything on this one thread.
            (_client, _captureClient) = ProcessLoopback.OpenAsync(_processId, includeProcessTree: true)
                .GetAwaiter().GetResult();

            _bufferEvent = new AutoResetEvent(false);
            int hr = _client.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
            if (hr != 0) throw new InvalidOperationException($"SetEventHandle falhou: 0x{hr:X8}");

            hr = _client.Start();
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.Start (process loopback) falhou: 0x{hr:X8}");

            Log.Write("ProcessCapture", $"capturando PID {_processId}, {SampleRate} Hz stereo float -> mono PCM16, orientado a evento.");
            ready.TrySetResult();

            PumpLoop(_cts!.Token);
        }
        catch (Exception ex)
        {
            Log.Write("ProcessCapture", $"loop de captura morreu: {ex}");
            ready.TrySetException(ex);
        }
    }

    private void PumpLoop(CancellationToken ct)
    {
        const int channels = ProcessLoopback.Channels;
        int chunkSamplesPerChannel = SampleRate / 10; // 100 ms
        var acc = new List<short>(chunkSamplesPerChannel * 2);
        var waitHandles = new[] { _bufferEvent!, ct.WaitHandle };

        while (!ct.IsCancellationRequested)
        {
            int signaled = WaitHandle.WaitAny(waitHandles, 500);
            if (signaled == WaitHandle.WaitTimeout || signaled == 1) continue; // 1 = cancellation

            // One event signal can mean several packets are ready — drain them all before waiting again.
            while (true)
            {
                _captureClient!.GetNextPacketSize(out int packetLength);
                if (packetLength == 0) break;

                _captureClient.GetBuffer(out IntPtr dataPtr, out int framesAvailable, out int flags, out _, out _);
                if (framesAvailable > 0)
                {
                    var floats = new float[framesAvailable * channels]; // zero-filled = silence by default
                    bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    if (!silent && dataPtr != IntPtr.Zero) Marshal.Copy(dataPtr, floats, 0, floats.Length);

                    for (int f = 0; f < framesAvailable; f++)
                    {
                        float avg = 0;
                        for (int c = 0; c < channels; c++) avg += floats[f * channels + c];
                        avg /= channels;
                        acc.Add((short)Math.Clamp(avg * 32767f, -32768f, 32767f));

                        if (acc.Count >= chunkSamplesPerChannel)
                        {
                            var chunkShorts = acc.ToArray();
                            acc.Clear();
                            var bytes = new byte[chunkShorts.Length * 2];
                            Buffer.BlockCopy(chunkShorts, 0, bytes, 0, bytes.Length);
                            Level?.Invoke(Rms(chunkShorts));
                            if (!Muted) ChunkAvailable?.Invoke(bytes);
                        }
                    }
                }
                _captureClient.ReleaseBuffer(framesAvailable);
            }
        }
    }

    private static float Rms(short[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sum = 0;
        foreach (var s in samples) { float f = s / 32768f; sum += f * f; }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _client?.Stop(); } catch { }
        try { _bufferEvent?.Dispose(); } catch { }
    }
}
