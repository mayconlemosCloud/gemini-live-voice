using System.Diagnostics;

namespace GeminiTranslateV2;

/// <summary>
/// Mede o atraso que o ouvinte realmente sente, de duas formas complementares.
///
/// 1) POR EVENTO (o log "ATRASO"): do instante em que a fala COMEÇA (primeiro chunk acima do
///    limiar depois de um silêncio) até a primeira tradução falada. É a medida precisa, mas só
///    acontece quando existe uma pausa de <see cref="GapMs"/> nas DUAS pontas ao mesmo tempo —
///    numa reunião real, em que a outra pessoa fala em blocos longos, isso quase nunca ocorre.
///    Medido nos logs: a direção "Entrada" rendia ~metade das medições da "Saída", e entre elas
///    passavam minutos. Serve para o log; não serve para um indicador que precisa estar sempre
///    preenchido.
///
/// 2) CONTÍNUA (<see cref="LastLag"/>, o que a UI mostra): correlaciona o ENVELOPE DE ENERGIA do
///    que foi enviado com o do dub que voltou, procurando o deslocamento que melhor os alinha.
///    Sílabas e respirações modulam os dois envelopes da mesma maneira, então o alinhamento
///    aparece mesmo sem nenhuma pausa — que é exatamente o caso em que a medição por evento
///    desiste. Funciona durante fala contínua e devolve um número novo a cada janela.
///
/// O buffer de reprodução (AudioOut.TranslationQueue) sozinho nunca serviu para isto: o servidor
/// devolve o dub em 1× tempo real, então a fila enche na mesma velocidade em que esvazia e fica
/// sempre rasa. O atraso mora ANTES dela (fila de envio + modelo), e as duas medidas acima o
/// enxergam somando o que ainda resta na fila.
/// </summary>
public sealed class LatencyProbe
{
    private const float SpeechRms = 0.02f;      // acima disso é fala, não ruído de fundo
    private const int GapMs = 700;              // silêncio que separa uma frase da seguinte

    private const int SentRate = Resample16k.Rate; // o que Spoke recebe
    private const int DubRate = 24000;             // o que Heard recebe

    // ---- estimador contínuo ----

    /// <summary>Resolução do envelope. 50 ms resolve sílaba sem virar ruído de amostragem.</summary>
    private const int BinMs = 50;

    /// <summary>Histórico circular: 20 s, o bastante para a janela de correlação e o atraso máximo.</summary>
    private const int Bins = 400;

    private const int MinLagBins = 200 / BinMs;    // 0,2 s — abaixo disso não é atraso plausível
    private const int MaxLagBins = 8000 / BinMs;   // 8 s — acima disso a sessão está quebrada
    private const int WindowBins = 10_000 / BinMs; // 10 s de sobreposição para correlacionar

    /// <summary>
    /// Suavização do envelope antes de correlacionar (300 ms). O dub é uma TRADUÇÃO: as sílabas
    /// dele não caem no mesmo lugar que as do original, então a modulação silábica (~4 Hz) é ruído
    /// para este alinhamento. O que casa de verdade é a estrutura de frase — quando se fala e
    /// quando se cala — e é ela que sobra depois deste filtro.
    /// </summary>
    private const int SmoothBins = 300 / BinMs;

    /// <summary>
    /// Correlação mínima para acreditar no alinhamento. Abaixo disso o que existe é ruído ou
    /// silêncio, e o valor anterior (envelhecendo) descreve melhor a chamada do que um chute novo.
    /// </summary>
    private const double MinScore = 0.35;

    /// <summary>
    /// Quão perto do pico máximo um pico anterior precisa chegar para ser preferido a ele. Existe
    /// por causa da ambiguidade de harmônico em fala rítmica (ver EstimateLocked). Baixo demais
    /// morde ruído e reporta atraso curto de menos; alto demais não resolve o harmônico.
    /// </summary>
    private const double PeakTolerance = 0.97;

    /// <summary>As duas pontas precisam estar vivas nesta janela para a estimativa valer.</summary>
    private const int FreshBins = 2000 / BinMs;

    /// <summary>Não recalcula mais que isso — o cálculo é O(lags × janela) e a UI pede a 2 Hz.</summary>
    private const double RecomputeMs = 250;

    private readonly string _tag;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastSpokeMs = double.NegativeInfinity;
    private double _lastHeardMs = double.NegativeInfinity;
    private double _pendingStartMs = double.NaN; // frase falada ainda sem tradução medida
    private readonly object _lock = new();

    private double _lastLagMs = double.NaN;
    private double _lastLagAtMs = double.NegativeInfinity;

    private readonly float[] _sentEnv = new float[Bins];
    private readonly float[] _dubEnv = new float[Bins];
    private long _sentBin = -1;
    private long _dubBin = -1;
    private double _playoutMs;
    private double _nextRecomputeMs;

    private double[] _sentFlat = Array.Empty<double>();
    private double[] _dubFlat = Array.Empty<double>();
    private double[] _tmp = Array.Empty<double>();

    public LatencyProbe(string tag) => _tag = tag;

    /// <summary>
    /// Último atraso conhecido de ponta a ponta e há quanto tempo ele foi apurado. A idade importa:
    /// numa pausa longa nada novo é medido, e quem exibe isto deve mostrar o valor como
    /// desatualizado em vez de apagá-lo — apagar deixa o indicador vazio justamente quando o
    /// usuário olha para ele.
    /// </summary>
    public (double LagMs, double AgeMs) LastLag()
    {
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now >= _nextRecomputeMs)
            {
                _nextRecomputeMs = now + RecomputeMs;
                double lag = EstimateLocked();
                if (!double.IsNaN(lag))
                {
                    // Suavização: o pico da correlação pula de bin em bin e um número que treme
                    // é ilegível. 0,35 converge em ~1,5 s sem esconder mudança real.
                    _lastLagMs = double.IsNaN(_lastLagMs) ? lag : _lastLagMs * 0.65 + lag * 0.35;
                    _lastLagAtMs = now;
                }
            }
            return (_lastLagMs, now - _lastLagAtMs);
        }
    }

    /// <summary>Chunk que ACABOU de ser enviado (16 kHz mono PCM16).</summary>
    public void Spoke(byte[] pcm)
    {
        float rms = Rms(pcm);
        double now = _clock.Elapsed.TotalMilliseconds;
        int spanBins = Math.Max(1, pcm.Length / 2 * 1000 / SentRate / BinMs);

        lock (_lock)
        {
            Mark(_sentEnv, ref _sentBin, (long)(now / BinMs), spanBins, rms);

            if (rms < SpeechRms) return;
            bool newPhrase = now - _lastSpokeMs > GapMs;
            _lastSpokeMs = now;
            // Uma frase pendente há mais de 15 s não vai mais ser casada com nada (o dub veio
            // abaixo do limiar, ou a sessão reconectou) — descarta para não reportar lixo.
            if (!double.IsNaN(_pendingStartMs) && now - _pendingStartMs > 15_000) _pendingStartMs = double.NaN;
            if (newPhrase && double.IsNaN(_pendingStartMs)) _pendingStartMs = now;
        }
    }

    /// <summary>
    /// Chunk de tradução que ACABOU de chegar (24 kHz mono PCM16). <paramref name="playoutMs"/> é
    /// o que já está na fila de reprodução: as duas medidas param na CHEGADA, e essa fila é o que
    /// ainda se soma antes de sair no fone.
    /// </summary>
    public void Heard(byte[] pcm, int outboxBacklog, double playoutMs)
    {
        float rms = Rms(pcm);
        double now = _clock.Elapsed.TotalMilliseconds;
        int spanBins = Math.Max(1, pcm.Length / 2 * 1000 / DubRate / BinMs);
        double start;

        lock (_lock)
        {
            Mark(_dubEnv, ref _dubBin, (long)(now / BinMs), spanBins, rms);
            _playoutMs = playoutMs;

            if (rms < SpeechRms) return;
            bool newPhrase = now - _lastHeardMs > GapMs;
            _lastHeardMs = now;
            if (!newPhrase || double.IsNaN(_pendingStartMs)) return;
            start = _pendingStartMs;
            _pendingStartMs = double.NaN;
        }

        // backlog alto é o app segurando áudio, não o modelo.
        double backlogMs = outboxBacklog * (double)LiveClient.ChunkMs;
        Log.Write(_tag, $"ATRASO {(now - start + playoutMs) / 1000:0.00} s da fala até o fone " +
                        $"= {(now - start) / 1000:0.00} s até chegar + {playoutMs:0} ms na fila de " +
                        $"reprodução (fila de envio {backlogMs:0} ms).");
    }

    /// <summary>
    /// Escreve <paramref name="v"/> nos bins cobertos por um chunk que terminou em
    /// <paramref name="endBin"/>, zerando o intervalo que passou sem áudio nenhum. Sem esse
    /// zeramento o buffer circular devolveria dado antigo como se fosse atual.
    /// </summary>
    private static void Mark(float[] env, ref long last, long endBin, int span, float v)
    {
        // O bin 0 é o instante em que o relógio do probe começou: não existe bin negativo, e nos
        // primeiros chunks de uma sessão endBin - span cai abaixo de zero.
        if (endBin < 0) return;
        if (last < 0) last = Math.Max(-1, endBin - span);
        if (endBin > last)
        {
            for (long b = Math.Max(Math.Max(last + 1, 0), endBin - Bins + 1); b <= endBin; b++)
                env[(int)(b % Bins)] = 0f;
            last = endBin;
        }
        for (long b = Math.Max(Math.Max(endBin - span + 1, 0), endBin - Bins + 1); b <= endBin; b++)
        {
            int i = (int)(b % Bins);
            if (v > env[i]) env[i] = v;
        }
    }

    /// <summary>
    /// Deslocamento que melhor alinha o envelope do dub ao do que foi enviado, em ms, já somada a
    /// fila de reprodução. NaN quando não há histórico ou confiança suficiente.
    /// </summary>
    private double EstimateLocked()
    {
        if (_sentBin < 0 || _dubBin < 0) return double.NaN;

        long nowBin = (long)(_clock.Elapsed.TotalMilliseconds / BinMs);
        // As duas pontas precisam estar correndo agora; numa pausa não há o que alinhar.
        if (nowBin - _sentBin > FreshBins || nowBin - _dubBin > FreshBins) return double.NaN;

        long end = Math.Min(_sentBin, _dubBin);
        long oldest = end - WindowBins + 1 - MaxLagBins;
        if (oldest < 0 || oldest <= _sentBin - Bins || oldest <= _dubBin - Bins) return double.NaN;

        // Desenrola o buffer circular numa faixa linear e suaviza, uma vez só para todos os lags.
        int span = (int)(end - oldest + 1);
        if (_sentFlat.Length < span) { _sentFlat = new double[span]; _dubFlat = new double[span]; _tmp = new double[span]; }
        for (int k = 0; k < span; k++)
        {
            int i = (int)((oldest + k) % Bins);
            _sentFlat[k] = _sentEnv[i];
            _dubFlat[k] = _dubEnv[i];
        }
        Smooth(_sentFlat, span, _tmp);
        Smooth(_dubFlat, span, _tmp);

        double bestScore = double.NegativeInfinity;
        int bestLag = -1;
        Span<double> scores = stackalloc double[MaxLagBins + 1];

        for (int lag = MinLagBins; lag <= MaxLagBins; lag++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < WindowBins; i++)
            {
                double x = _dubFlat[span - 1 - i];
                double y = _sentFlat[span - 1 - i - lag];
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            }
            double n = WindowBins;
            double cov = sxy - sx * sy / n;
            double vx = sxx - sx * sx / n;
            double vy = syy - sy * sy / n;
            double r = (vx <= 1e-12 || vy <= 1e-12) ? 0 : cov / Math.Sqrt(vx * vy);
            scores[lag] = r;
            if (r > bestScore) { bestScore = r; bestLag = lag; }
        }

        if (bestLag < 0 || bestScore < MinScore) return double.NaN;

        // Fala é RÍTMICA. Se as frases se repetem a cada ~3 s, a correlação tem picos quase iguais
        // em lag, lag+3 s, lag+6 s… e o máximo global cai num harmônico com facilidade — medido:
        // com frases perfeitamente periódicas de 3,35 s, um atraso real de 3,5 s era reportado como
        // 6,75 s. Entre picos praticamente empatados o verdadeiro é sempre o MENOR: nenhuma
        // tradução sai antes do que a explica.
        double accept = bestScore * PeakTolerance;
        for (int lag = MinLagBins + 1; lag < bestLag; lag++)
        {
            if (scores[lag] < accept) continue;
            if (scores[lag] < scores[lag - 1] || scores[lag] < scores[lag + 1]) continue; // não é pico
            bestLag = lag;
            break;
        }

        // Interpolação parabólica no pico: sem ela o valor só se move em degraus de 50 ms.
        double refined = bestLag;
        if (bestLag > MinLagBins && bestLag < MaxLagBins)
        {
            double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
            double denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-12) refined = bestLag + 0.5 * (a - c) / denom;
        }

        return refined * BinMs + _playoutMs;
    }

    /// <summary>Média móvel de <see cref="SmoothBins"/> bins, no lugar.</summary>
    private static void Smooth(double[] x, int len, double[] tmp)
    {
        double sum = 0;
        for (int i = 0; i < len; i++)
        {
            sum += x[i];
            if (i >= SmoothBins) sum -= x[i - SmoothBins];
            tmp[i] = sum / Math.Min(i + 1, SmoothBins);
        }
        Array.Copy(tmp, x, len);
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
