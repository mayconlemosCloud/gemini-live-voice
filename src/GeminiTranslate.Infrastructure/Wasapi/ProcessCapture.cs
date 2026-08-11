using System.Runtime.InteropServices;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;
using GeminiTranslate.Infrastructure.Windows;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Captura APENAS o áudio renderizado por um processo (Teams.exe, chrome.exe, WhatsApp.exe) via
/// Process Loopback do Windows — e não o sistema inteiro, como faria um cabo virtual.
/// </summary>
/// <remarks>
/// É isso que faz o silêncio entre as frases da outra pessoa chegar ao modelo como silêncio
/// digital verdadeiro, em vez do piso de ruído do mix do sistema, que causava o defeito de "não
/// para de falar" em versões anteriores.
///
/// ORIENTADO A EVENTO, NÃO A POLLING: a primeira versão funcional usava GetNextPacketSize com
/// Task.Delay(10) numa thread comum do pool, e a comparação lado a lado contra uma captura real
/// de aba de navegador mostrou que era perceptivelmente menos fluida. Polling com sleep numa
/// thread do pool não tem garantia de tempo: sob qualquer disputa — E/S do WebSocket, escrita de
/// WAV, interface — o giro de 10 ms escorrega, produzindo exatamente o tipo de jitter que uma
/// captura de baixa latência evita. O WASAPI oferece modo orientado a evento
/// (AUDCLNT_STREAMFLAGS_EVENTCALLBACK) precisamente para isso: o sistema sinaliza um handle no
/// instante em que há buffer pronto, sem dormir nem adivinhar.
/// </remarks>
public sealed class ProcessCapture : IAudioSource
{
    private const int BufferFlagsSilent = 0x2;
    private const int WaitTimeoutMs = 500;

    private readonly uint _processId;
    private ProcessLoopbackInterop.IAudioClient? _client;
    private ProcessLoopbackInterop.IAudioCaptureClient? _capture;
    private AutoResetEvent? _bufferReady;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <inheritdoc />
    public int SampleRate => ProcessLoopbackInterop.SampleRate;

    /// <inheritdoc />
    public event Action<byte[]>? ChunkAvailable;

    /// <inheritdoc />
    public event Action<float>? Level;

    /// <inheritdoc />
    public bool Muted { get; set; }

    /// <param name="processId">Processo cujo áudio será capturado.</param>
    public ProcessCapture(uint processId) => _processId = processId;

    /// <summary>
    /// Ativa a captura numa thread dedicada e de prioridade elevada.
    /// </summary>
    /// <remarks>
    /// A ativação e o laço de captura correm na MESMA thread, nunca na thread do Dispatcher do
    /// WPF: separá-las já causou um E_NOINTERFACE real aqui.
    /// </remarks>
    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "ProcessCapture"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        return ready.Task;
    }

    private void Run(TaskCompletionSource ready)
    {
        try
        {
            Open();
            Log.Write("ProcessCapture", $"capturando PID {_processId}, {SampleRate} Hz stereo float " +
                                        "-> mono PCM16, orientado a evento.");
            ready.TrySetResult();
            Pump(_cts!.Token);
        }
        catch (Exception ex)
        {
            Log.Write("ProcessCapture", $"loop de captura morreu: {ex}");
            ready.TrySetException(ex);
        }
    }

    /// <summary>
    /// Ativa o cliente de forma síncrona. A abertura assíncrona só aguarda um callback COM, sem
    /// trabalho real, então bloquear aqui mantém tudo na mesma thread.
    /// </summary>
    private void Open()
    {
        (_client, _capture) = ProcessLoopbackInterop
            .OpenAsync(_processId, includeProcessTree: true)
            .GetAwaiter().GetResult();

        _bufferReady = new AutoResetEvent(false);

        int hr = _client.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());
        if (hr != 0) throw new InvalidOperationException($"SetEventHandle falhou: 0x{hr:X8}");

        hr = _client.Start();
        if (hr != 0) throw new InvalidOperationException($"IAudioClient.Start (process loopback) falhou: 0x{hr:X8}");
    }

    private void Pump(CancellationToken ct)
    {
        var accumulator = new ChunkAccumulator(CaptureChunk.Bytes(SampleRate) / Pcm.BytesPerSample, Publish);
        var waitHandles = new[] { _bufferReady!, ct.WaitHandle };

        while (!ct.IsCancellationRequested)
        {
            int signaled = WaitHandle.WaitAny(waitHandles, WaitTimeoutMs);
            if (signaled != 0) continue;

            DrainReadyPackets(accumulator);
        }
    }

    /// <summary>
    /// Um único sinal de evento pode significar vários pacotes prontos — esvazia todos antes de
    /// voltar a esperar.
    /// </summary>
    private void DrainReadyPackets(ChunkAccumulator accumulator)
    {
        while (true)
        {
            _capture!.GetNextPacketSize(out int packetLength);
            if (packetLength == 0) return;

            _capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _);
            if (frames > 0) Accumulate(accumulator, data, frames, flags);
            _capture.ReleaseBuffer(frames);
        }
    }

    /// <summary>Faz a média dos canais do pacote e alimenta o acumulador de chunks.</summary>
    private static void Accumulate(ChunkAccumulator accumulator, IntPtr data, int frames, int flags)
    {
        const int channels = ProcessLoopbackInterop.Channels;

        var samples = new float[frames * channels];
        bool silent = (flags & BufferFlagsSilent) != 0;
        if (!silent && data != IntPtr.Zero) Marshal.Copy(data, samples, 0, samples.Length);

        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += samples[f * channels + c];
            accumulator.Add(sum / channels);
        }
    }

    private void Publish(byte[] chunk)
    {
        Level?.Invoke(Pcm.Rms(chunk));
        if (!Muted) ChunkAvailable?.Invoke(chunk);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _client?.Stop(); } catch { }
        try { _bufferReady?.Dispose(); } catch { }
    }

    /// <summary>Junta amostras avulsas até fechar um chunk e o entrega como PCM16.</summary>
    private sealed class ChunkAccumulator(int samplesPerChunk, Action<byte[]> onChunk)
    {
        private readonly byte[] _chunk = new byte[samplesPerChunk * Pcm.BytesPerSample];
        private int _count;

        /// <param name="sample">Amostra mono normalizada, de -1 a 1.</param>
        public void Add(float sample)
        {
            Pcm.WriteSample(_chunk, _count++, sample);
            if (_count < samplesPerChunk) return;

            _count = 0;
            onChunk((byte[])_chunk.Clone());
        }
    }
}
