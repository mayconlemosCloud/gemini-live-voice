using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslateV2;

/// <summary>
/// Converte os chunks capturados (mono PCM16 na taxa nativa da placa, normalmente 48 kHz) para
/// 16 kHz mono PCM16, que é a taxa de ENTRADA documentada da Live API.
///
/// Isso não é cosmético: a 48 kHz cada direção envia ~96 KB/s de PCM, que viram ~128 KB/s depois
/// do base64 — as duas direções somam ~2 Mbps de upload CONTÍNUO, competindo com o Teams/Meet no
/// mesmo link. Quando o uplink não dá conta, o WebSocket aplica contrapressão, a fila de envio
/// cresce em silêncio e o atraso vira permanente (o servidor devolve o dub em 1× real, então
/// atraso acumulado nunca é recuperado). A 16 kHz o mesmo áudio ocupa 1/3 disso.
/// Nada se perde: a Live API reamostra tudo para 16 kHz do lado dela de qualquer jeito.
///
/// A voz original que toca por baixo da tradução continua na taxa nativa — só o que vai para a
/// rede passa por aqui.
/// </summary>
public sealed class Resample16k
{
    public const int Rate = 16000;

    private readonly BufferedWaveProvider _in;
    private readonly IWaveProvider _out;
    private byte[] _buf = new byte[4096];

    public Resample16k(int inputRate)
    {
        _in = new BufferedWaveProvider(new WaveFormat(inputRate, 16, 1))
        {
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };

        ISampleProvider sp = _in.ToSampleProvider();
        if (inputRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
        _out = new SampleToWaveProvider16(sp);
    }

    /// <summary>
    /// Entra um chunk na taxa nativa, sai o equivalente a 16 kHz (array novo, tamanho variável;
    /// null quando ainda não há amostras suficientes para um bloco). Stateful: chame sempre na
    /// mesma ordem dos chunks capturados.
    /// </summary>
    public byte[]? Feed(byte[] chunk)
    {
        _in.AddSamples(chunk, 0, chunk.Length);

        // Teto do que pode sair: proporção das taxas, com folga para o atraso do filtro.
        int max = (int)((long)chunk.Length * Rate / _in.WaveFormat.SampleRate) + 256;
        max &= ~1; // sempre um número inteiro de amostras de 16 bits
        if (_buf.Length < max) _buf = new byte[max];

        int read = _out.Read(_buf, 0, max);
        if (read <= 0) return null;

        var outChunk = new byte[read];
        Buffer.BlockCopy(_buf, 0, outChunk, 0, read);
        return outChunk;
    }
}
