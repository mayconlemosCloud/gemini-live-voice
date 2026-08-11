using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Base das capturas WASAPI: acumula o que o dispositivo entrega, converte para mono PCM16 na
/// taxa nativa e emite chunks de <see cref="CaptureChunk.DurationMs"/> ms.
/// </summary>
/// <remarks>
/// Microfone e loopback de dispositivo diferem apenas em qual <see cref="WasapiCapture"/> abrir
/// e em como reduzir os canais a mono. Todo o resto — buffer, laço de bombeamento, fatiamento em
/// chunks, medição de nível e descarte — é idêntico, e viver aqui garante que os dois lados da
/// conversa sejam capturados exatamente da mesma forma.
/// </remarks>
public abstract class WasapiAudioSource : IAudioSource
{
    private readonly string _tag;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly IWaveProvider _mono;
    private readonly int _chunkBytes;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public event Action<byte[]>? ChunkAvailable;

    /// <inheritdoc />
    public event Action<float>? Level;

    /// <inheritdoc />
    public bool Muted { get; set; }

    /// <param name="capture">Captura já configurada para o dispositivo desejado.</param>
    /// <param name="tag">Origem exibida no log.</param>
    /// <param name="deviceName">Nome amigável do dispositivo, para o log de abertura.</param>
    protected WasapiAudioSource(WasapiCapture capture, string tag, string deviceName)
    {
        _tag = tag;
        _capture = capture;
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

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1) samples = ToMono(samples);
        SampleRate = samples.WaveFormat.SampleRate;
        _chunkBytes = CaptureChunk.Bytes(SampleRate);
        _mono = new SampleToWaveProvider16(samples);

        Log.Write(_tag, $"captura em '{deviceName}' ({_capture.WaveFormat.SampleRate} Hz " +
                        $"{_capture.WaveFormat.Channels} ch), mono a {SampleRate} Hz " +
                        $"(a rede recebe {AudioRates.Wire} Hz — ver WireResampler).");
    }

    /// <summary>
    /// Reduz um fluxo multicanal a mono. A escolha é do dispositivo: um mix produzido pode ser
    /// somado, um arranjo de microfones não.
    /// </summary>
    protected abstract ISampleProvider ToMono(ISampleProvider source);

    /// <inheritdoc />
    public Task StartAsync()
    {
        _capture.StartRecording();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await PumpCoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write(_tag, $"loop de captura morreu: {ex}");
        }
    }

    /// <summary>
    /// Lê o fluxo mono e emite chunks completos, guardando a sobra para o próximo giro — o
    /// dispositivo não entrega múltiplos exatos do tamanho de chunk.
    /// </summary>
    private async Task PumpCoreAsync(CancellationToken ct)
    {
        var read = new byte[_chunkBytes];
        var pending = new byte[_chunkBytes * 4];
        int pendingLength = 0;

        while (!ct.IsCancellationRequested)
        {
            int got = _mono.Read(read, 0, read.Length);
            if (got <= 0)
            {
                try { await Task.Delay(10, ct); } catch { break; }
                continue;
            }

            if (pendingLength + got > pending.Length) pendingLength = 0;
            Buffer.BlockCopy(read, 0, pending, pendingLength, got);
            pendingLength += got;

            int offset = 0;
            while (pendingLength - offset >= _chunkBytes)
            {
                Emit(pending, offset);
                offset += _chunkBytes;
            }

            pendingLength -= offset;
            if (pendingLength > 0) Buffer.BlockCopy(pending, offset, pending, 0, pendingLength);
        }
    }

    /// <summary>
    /// Publica um chunk. O nível é medido mesmo mudo, para o medidor continuar respondendo.
    /// </summary>
    private void Emit(byte[] source, int offset)
    {
        Level?.Invoke(Pcm.Rms(source, offset, _chunkBytes));
        if (Muted) return;

        var chunk = new byte[_chunkBytes];
        Buffer.BlockCopy(source, offset, chunk, 0, _chunkBytes);
        ChunkAvailable?.Invoke(chunk);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
