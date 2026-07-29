using System.Diagnostics;

namespace GeminiTranslateV2;

/// <summary>
/// Mede o atraso que o ouvinte realmente sente: do instante em que a fala COMEÇA (primeiro chunk
/// acima do limiar depois de um silêncio) até o instante em que chega a primeira tradução falada
/// (primeiro chunk recebido acima do limiar depois de um silêncio no dub).
///
/// Sem isso a discussão de latência é chute: o buffer de reprodução (AudioOut.TranslationQueue)
/// fica sempre raso, porque o servidor devolve um dub CONTÍNUO em 1× tempo real — ele enche na
/// mesma velocidade em que esvazia. O atraso mora ANTES do buffer (fila de envio + modelo), então
/// medir profundidade de buffer não enxerga nada. Este probe mede as duas pontas.
/// </summary>
public sealed class LatencyProbe
{
    private const float SpeechRms = 0.02f;      // acima disso é fala, não ruído de fundo
    private const int GapMs = 700;              // silêncio que separa uma frase da seguinte

    private readonly string _tag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastSpokeMs = double.NegativeInfinity;
    private double _lastHeardMs = double.NegativeInfinity;
    private double _pendingStartMs = double.NaN; // frase falada ainda sem tradução medida
    private readonly object _lock = new();

    public LatencyProbe(string tag) => _tag = tag;

    /// <summary>Chunk que ACABOU de ser enviado (16 kHz mono PCM16).</summary>
    public void Spoke(byte[] pcm)
    {
        if (Rms(pcm) < SpeechRms) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        lock (_lock)
        {
            bool newPhrase = now - _lastSpokeMs > GapMs;
            _lastSpokeMs = now;
            // Uma frase pendente há mais de 15 s não vai mais ser casada com nada (o dub veio
            // abaixo do limiar, ou a sessão reconectou) — descarta para não reportar lixo.
            if (!double.IsNaN(_pendingStartMs) && now - _pendingStartMs > 15_000) _pendingStartMs = double.NaN;
            if (newPhrase && double.IsNaN(_pendingStartMs)) _pendingStartMs = now;
        }
    }

    /// <summary>Chunk de tradução que ACABOU de chegar (24 kHz mono PCM16).</summary>
    public void Heard(byte[] pcm, int outboxBacklog)
    {
        if (Rms(pcm) < SpeechRms) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        double start;
        lock (_lock)
        {
            bool newPhrase = now - _lastHeardMs > GapMs;
            _lastHeardMs = now;
            if (!newPhrase || double.IsNaN(_pendingStartMs)) return;
            start = _pendingStartMs;
            _pendingStartMs = double.NaN;
        }

        // 40 ms por chunk na fila de envio — backlog alto é o app segurando áudio, não o modelo.
        double backlogMs = outboxBacklog * 40.0;
        Log.Write(_tag, $"ATRASO {(now - start) / 1000:0.00} s da fala até a tradução " +
                        $"(fila de envio {backlogMs:0} ms).");
    }

    private static float Rms(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float f = s / 32768f;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / samples);
    }
}
