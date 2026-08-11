using System.Diagnostics;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Mede o atraso que o ouvinte realmente sente, de duas formas complementares.
/// </summary>
/// <remarks>
/// POR EVENTO (o log "ATRASO"): do instante em que a fala começa — primeiro chunk acima do
/// limiar depois de um silêncio — até a primeira tradução falada. É a medida precisa, mas só
/// acontece quando existe uma pausa nas DUAS pontas ao mesmo tempo, e numa reunião real, em que
/// a outra pessoa fala em blocos longos, isso quase nunca ocorre: medido nos logs, a direção
/// "Entrada" rendia metade das medições da "Saída", e entre elas passavam minutos. Serve para o
/// log; não serve para um indicador que precisa estar sempre preenchido.
///
/// CONTÍNUA (<see cref="LastLag"/>, o que a interface mostra): delegada ao
/// <see cref="EnvelopeLagEstimator"/>, que funciona durante fala contínua.
///
/// A fila de reprodução sozinha nunca serviu para isto: o servidor devolve o dub em 1× tempo
/// real, então ela enche na mesma velocidade em que esvazia e fica sempre rasa. O atraso mora
/// ANTES dela — na fila de envio e no modelo — e as duas medidas acima o enxergam, somando o que
/// ainda resta na fila.
/// </remarks>
public sealed class LatencyProbe
{
    /// <summary>Acima deste RMS é fala, não ruído de fundo.</summary>
    private const float SpeechRms = 0.02f;

    /// <summary>Silêncio que separa uma frase da seguinte.</summary>
    private const int PhraseGapMs = 700;

    /// <summary>Frase pendente há mais que isso não vai mais ser casada com nada.</summary>
    private const double PendingExpiryMs = 15_000;

    /// <summary>Não recalcula mais que isso — o cálculo é caro e a interface pede a 2 Hz.</summary>
    private const double RecomputeMs = 250;

    /// <summary>
    /// Peso da medição nova na suavização. O pico da correlação pula de bin em bin e um número
    /// que treme é ilegível; 0,35 converge em cerca de 1,5 s sem esconder mudança real.
    /// </summary>
    private const double SmoothingWeight = 0.35;

    private readonly string _tag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly EnvelopeLagEstimator _estimator = new();
    private readonly object _gate = new();

    private double _lastSpokeAtMs = double.NegativeInfinity;
    private double _lastHeardAtMs = double.NegativeInfinity;
    private double _pendingStartMs = double.NaN;

    private double _lagMs = double.NaN;
    private double _lagAtMs = double.NegativeInfinity;
    private double _playoutMs;
    private double _nextRecomputeMs;

    /// <param name="tag">Origem exibida no log.</param>
    public LatencyProbe(string tag) => _tag = tag;

    /// <summary>
    /// Último atraso conhecido de ponta a ponta e há quanto tempo ele foi apurado.
    /// </summary>
    /// <remarks>
    /// A idade importa: numa pausa longa nada novo é medido, e quem exibe isto deve mostrar o
    /// valor como desatualizado em vez de apagá-lo — apagar deixa o indicador vazio justamente
    /// quando o usuário olha para ele.
    /// </remarks>
    public (double LagMs, double AgeMs) LastLag()
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now >= _nextRecomputeMs)
            {
                _nextRecomputeMs = now + RecomputeMs;
                Recompute(now);
            }
            return (_lagMs, now - _lagAtMs);
        }
    }

    private void Recompute(double now)
    {
        double measured = _estimator.EstimateMs(now, _playoutMs);
        if (double.IsNaN(measured)) return;

        _lagMs = double.IsNaN(_lagMs)
            ? measured
            : _lagMs * (1 - SmoothingWeight) + measured * SmoothingWeight;
        _lagAtMs = now;
    }

    /// <summary>Chunk de 16 kHz mono PCM16 que acabou de ser enviado à rede.</summary>
    public void Spoke(byte[] pcm)
    {
        float rms = Pcm.Rms(pcm);
        double now = _clock.Elapsed.TotalMilliseconds;
        int spanBins = SpanBins(pcm, AudioRates.Wire);

        lock (_gate)
        {
            _estimator.MarkSent(now, spanBins, rms);
            if (rms < SpeechRms) return;

            bool newPhrase = now - _lastSpokeAtMs > PhraseGapMs;
            _lastSpokeAtMs = now;

            if (!double.IsNaN(_pendingStartMs) && now - _pendingStartMs > PendingExpiryMs)
                _pendingStartMs = double.NaN;
            if (newPhrase && double.IsNaN(_pendingStartMs)) _pendingStartMs = now;
        }
    }

    /// <summary>Chunk de tradução de 24 kHz mono PCM16 que acabou de chegar.</summary>
    /// <param name="pcm">O áudio recebido.</param>
    /// <param name="outboxBacklog">Chunks ainda esperando para ir à rede, para o log.</param>
    /// <param name="playoutMs">
    /// O que já está na fila de reprodução. As duas medidas param na CHEGADA, e essa fila é o que
    /// ainda se soma antes de sair no fone.
    /// </param>
    public void Heard(byte[] pcm, int outboxBacklog, double playoutMs)
    {
        float rms = Pcm.Rms(pcm);
        double now = _clock.Elapsed.TotalMilliseconds;
        int spanBins = SpanBins(pcm, AudioRates.Dub);
        double phraseStart;

        lock (_gate)
        {
            _estimator.MarkDub(now, spanBins, rms);
            _playoutMs = playoutMs;

            if (rms < SpeechRms) return;

            bool newPhrase = now - _lastHeardAtMs > PhraseGapMs;
            _lastHeardAtMs = now;
            if (!newPhrase || double.IsNaN(_pendingStartMs)) return;

            phraseStart = _pendingStartMs;
            _pendingStartMs = double.NaN;
        }

        LogPhraseLatency(now - phraseStart, playoutMs, outboxBacklog);
    }

    /// <summary>Backlog alto no log significa o app segurando áudio, não o modelo demorando.</summary>
    private void LogPhraseLatency(double arrivalMs, double playoutMs, int outboxBacklog)
    {
        double backlogMs = outboxBacklog * (double)CaptureChunk.DurationMs;
        Log.Write(_tag, $"ATRASO {(arrivalMs + playoutMs) / 1000:0.00} s da fala até o fone " +
                        $"= {arrivalMs / 1000:0.00} s até chegar + {playoutMs:0} ms na fila de " +
                        $"reprodução (fila de envio {backlogMs:0} ms).");
    }

    private static int SpanBins(byte[] pcm, int sampleRate) =>
        Math.Max(1, (int)(Pcm.DurationMs(pcm, sampleRate) / EnvelopeLagEstimator.BinMs));
}
