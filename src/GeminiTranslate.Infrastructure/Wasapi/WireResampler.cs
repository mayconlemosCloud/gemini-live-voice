using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Signal;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Converte os chunks capturados (mono PCM16 na taxa nativa da placa, normalmente 48 kHz) para
/// os 16 kHz mono PCM16 que a Live API documenta como taxa de entrada.
/// </summary>
/// <remarks>
/// Não é cosmético. A 48 kHz cada direção envia ~96 KB/s de PCM, que viram ~128 KB/s depois do
/// base64 — as duas direções somam ~2 Mbps de upload contínuo, competindo com o Teams/Meet no
/// mesmo link. Quando o uplink não dá conta, o WebSocket aplica contrapressão, a fila de envio
/// cresce em silêncio e o atraso vira permanente, porque o servidor devolve o dub em 1× tempo
/// real e nada do que atrasou é recuperado. A 16 kHz o mesmo áudio ocupa um terço disso, e não
/// se perde nada: a Live API reamostra para 16 kHz do lado dela de qualquer forma.
///
/// Só o que vai para a rede passa por aqui — a voz original tocada por baixo da tradução
/// continua na taxa nativa.
/// </remarks>
public sealed class WireResampler : IResampler
{
    private readonly BufferedWaveProvider _input;
    private readonly IWaveProvider _output;
    private byte[] _scratch = new byte[4096];

    /// <param name="inputRate">Taxa nativa dos chunks que serão alimentados.</param>
    public WireResampler(int inputRate)
    {
        _input = new BufferedWaveProvider(new WaveFormat(inputRate, 16, 1))
        {
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };

        ISampleProvider samples = _input.ToSampleProvider();
        if (inputRate != AudioRates.Wire) samples = new WdlResamplingSampleProvider(samples, AudioRates.Wire);
        _output = new SampleToWaveProvider16(samples);
    }

    /// <inheritdoc />
    public byte[]? Feed(byte[] chunk)
    {
        _input.AddSamples(chunk, 0, chunk.Length);

        int max = (int)((long)chunk.Length * AudioRates.Wire / _input.WaveFormat.SampleRate) + 256;
        max &= ~1;
        if (_scratch.Length < max) _scratch = new byte[max];

        int read = _output.Read(_scratch, 0, max);
        if (read <= 0) return null;

        var resampled = new byte[read];
        Buffer.BlockCopy(_scratch, 0, resampled, 0, read);
        return resampled;
    }
}
