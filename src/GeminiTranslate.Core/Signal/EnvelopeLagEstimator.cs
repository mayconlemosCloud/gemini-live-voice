namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Estima o atraso entre o que foi enviado e o dub que voltou, correlacionando os ENVELOPES DE
/// ENERGIA dos dois e procurando o deslocamento que melhor os alinha.
/// </summary>
/// <remarks>
/// Sílabas e respirações modulam os dois envelopes da mesma maneira, então o alinhamento aparece
/// mesmo sem nenhuma pausa na conversa — que é justamente o caso em que uma medição por evento
/// (esperar silêncio dos dois lados) desiste. Funciona durante fala contínua e devolve um número
/// novo a cada janela.
///
/// Não é thread-safe: quem usa serializa o acesso.
/// </remarks>
public sealed class EnvelopeLagEstimator
{
    /// <summary>Resolução do envelope. 50 ms resolve sílaba sem virar ruído de amostragem.</summary>
    public const int BinMs = 50;

    /// <summary>Histórico circular de 20 s, o bastante para a janela de correlação e o atraso máximo.</summary>
    private const int Bins = 400;

    /// <summary>Abaixo de 0,2 s não é atraso plausível.</summary>
    private const int MinLagBins = 200 / BinMs;

    /// <summary>Acima de 8 s a sessão está quebrada, não atrasada.</summary>
    private const int MaxLagBins = 8000 / BinMs;

    /// <summary>Sobreposição de 10 s usada para correlacionar.</summary>
    private const int WindowBins = 10_000 / BinMs;

    /// <summary>
    /// Suavização do envelope antes de correlacionar, de 300 ms.
    /// </summary>
    /// <remarks>
    /// O dub é uma TRADUÇÃO: as sílabas dele não caem no mesmo lugar que as do original, então a
    /// modulação silábica (~4 Hz) é ruído para este alinhamento. O que casa de verdade é a
    /// estrutura de frase — quando se fala e quando se cala — e é ela que sobra depois do filtro.
    /// </remarks>
    private const int SmoothBins = 300 / BinMs;

    /// <summary>
    /// Correlação mínima para acreditar no alinhamento. Abaixo disso o que existe é ruído ou
    /// silêncio, e o valor anterior descreve melhor a chamada do que um chute novo.
    /// </summary>
    private const double MinScore = 0.35;

    /// <summary>
    /// Quão perto do pico máximo um pico anterior precisa chegar para ser preferido a ele.
    /// </summary>
    /// <remarks>
    /// Existe por causa da ambiguidade de harmônico em fala rítmica (ver <see cref="ResolveHarmonic"/>).
    /// Baixo demais morde ruído e reporta atraso curto de menos; alto demais não resolve o harmônico.
    /// </remarks>
    private const double PeakTolerance = 0.97;

    /// <summary>As duas pontas precisam estar vivas nesta janela para a estimativa valer.</summary>
    private const int FreshBins = 2000 / BinMs;

    private readonly float[] _sentEnvelope = new float[Bins];
    private readonly float[] _dubEnvelope = new float[Bins];
    private long _sentBin = -1;
    private long _dubBin = -1;

    private double[] _sentWindow = [];
    private double[] _dubWindow = [];
    private double[] _scratch = [];

    /// <summary>Registra a energia de um chunk enviado, terminando no instante informado.</summary>
    public void MarkSent(double nowMs, int spanBins, float rms) =>
        Mark(_sentEnvelope, ref _sentBin, (long)(nowMs / BinMs), spanBins, rms);

    /// <summary>Registra a energia de um chunk de tradução recebido, terminando no instante informado.</summary>
    public void MarkDub(double nowMs, int spanBins, float rms) =>
        Mark(_dubEnvelope, ref _dubBin, (long)(nowMs / BinMs), spanBins, rms);

    /// <summary>
    /// Deslocamento que melhor alinha o envelope do dub ao do que foi enviado, em milissegundos,
    /// já somada a <paramref name="playoutMs"/>. NaN quando não há histórico ou confiança.
    /// </summary>
    public double EstimateMs(double nowMs, double playoutMs)
    {
        if (!HasFreshBothEnds(nowMs)) return double.NaN;

        long end = Math.Min(_sentBin, _dubBin);
        long oldest = end - WindowBins + 1 - MaxLagBins;
        if (oldest < 0 || oldest <= _sentBin - Bins || oldest <= _dubBin - Bins) return double.NaN;

        int span = (int)(end - oldest + 1);
        Unroll(oldest, span);

        Span<double> scores = stackalloc double[MaxLagBins + 1];
        int bestLag = Correlate(span, scores, out double bestScore);
        if (bestLag < 0 || bestScore < MinScore) return double.NaN;

        bestLag = ResolveHarmonic(scores, bestLag, bestScore);
        return Refine(scores, bestLag) * BinMs + playoutMs;
    }

    /// <summary>As duas pontas precisam estar correndo agora; numa pausa não há o que alinhar.</summary>
    private bool HasFreshBothEnds(double nowMs)
    {
        if (_sentBin < 0 || _dubBin < 0) return false;

        long nowBin = (long)(nowMs / BinMs);
        return nowBin - _sentBin <= FreshBins && nowBin - _dubBin <= FreshBins;
    }

    /// <summary>
    /// Desenrola os dois buffers circulares numa faixa linear e os suaviza, uma vez só para
    /// todos os deslocamentos testados.
    /// </summary>
    private void Unroll(long oldest, int span)
    {
        if (_sentWindow.Length < span)
        {
            _sentWindow = new double[span];
            _dubWindow = new double[span];
            _scratch = new double[span];
        }

        for (int k = 0; k < span; k++)
        {
            int i = (int)((oldest + k) % Bins);
            _sentWindow[k] = _sentEnvelope[i];
            _dubWindow[k] = _dubEnvelope[i];
        }

        Smooth(_sentWindow, span, _scratch);
        Smooth(_dubWindow, span, _scratch);
    }

    /// <summary>
    /// Correlação de Pearson do dub contra o enviado, para cada deslocamento candidato.
    /// Devolve o deslocamento de maior pontuação, ou -1.
    /// </summary>
    private int Correlate(int span, Span<double> scores, out double bestScore)
    {
        bestScore = double.NegativeInfinity;
        int bestLag = -1;

        for (int lag = MinLagBins; lag <= MaxLagBins; lag++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < WindowBins; i++)
            {
                double x = _dubWindow[span - 1 - i];
                double y = _sentWindow[span - 1 - i - lag];
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            }

            double n = WindowBins;
            double covariance = sxy - sx * sy / n;
            double varianceX = sxx - sx * sx / n;
            double varianceY = syy - sy * sy / n;
            double r = varianceX <= 1e-12 || varianceY <= 1e-12
                ? 0
                : covariance / Math.Sqrt(varianceX * varianceY);

            scores[lag] = r;
            if (r > bestScore)
            {
                bestScore = r;
                bestLag = lag;
            }
        }
        return bestLag;
    }

    /// <summary>
    /// Entre picos praticamente empatados, prefere o MENOR deslocamento.
    /// </summary>
    /// <remarks>
    /// Fala é rítmica. Se as frases se repetem a cada ~3 s, a correlação tem picos quase iguais
    /// em lag, lag+3 s, lag+6 s, e o máximo global cai num harmônico com facilidade — medido: com
    /// frases perfeitamente periódicas de 3,35 s, um atraso real de 3,5 s era reportado como
    /// 6,75 s. O verdadeiro é sempre o menor, porque nenhuma tradução sai antes do que a explica.
    /// </remarks>
    private static int ResolveHarmonic(Span<double> scores, int bestLag, double bestScore)
    {
        double accept = bestScore * PeakTolerance;
        for (int lag = MinLagBins + 1; lag < bestLag; lag++)
        {
            if (scores[lag] < accept) continue;
            if (scores[lag] < scores[lag - 1] || scores[lag] < scores[lag + 1]) continue;
            return lag;
        }
        return bestLag;
    }

    /// <summary>
    /// Interpolação parabólica no pico. Sem ela o valor só se moveria em degraus de
    /// <see cref="BinMs"/> milissegundos.
    /// </summary>
    private static double Refine(Span<double> scores, int bestLag)
    {
        if (bestLag <= MinLagBins || bestLag >= MaxLagBins) return bestLag;

        double a = scores[bestLag - 1], b = scores[bestLag], c = scores[bestLag + 1];
        double denominator = a - 2 * b + c;
        return Math.Abs(denominator) > 1e-12
            ? bestLag + 0.5 * (a - c) / denominator
            : bestLag;
    }

    /// <summary>
    /// Escreve <paramref name="value"/> nos bins cobertos por um chunk que terminou em
    /// <paramref name="endBin"/>, zerando o intervalo que passou sem áudio nenhum.
    /// </summary>
    /// <remarks>
    /// Sem esse zeramento o buffer circular devolveria dado antigo como se fosse atual. O bin 0 é
    /// o instante em que o relógio começou: não existe bin negativo, e nos primeiros chunks de uma
    /// sessão o início do chunk cai abaixo de zero.
    /// </remarks>
    private static void Mark(float[] envelope, ref long last, long endBin, int span, float value)
    {
        if (endBin < 0) return;
        if (last < 0) last = Math.Max(-1, endBin - span);

        if (endBin > last)
        {
            for (long b = Math.Max(Math.Max(last + 1, 0), endBin - Bins + 1); b <= endBin; b++)
                envelope[(int)(b % Bins)] = 0f;
            last = endBin;
        }

        for (long b = Math.Max(Math.Max(endBin - span + 1, 0), endBin - Bins + 1); b <= endBin; b++)
        {
            int i = (int)(b % Bins);
            if (value > envelope[i]) envelope[i] = value;
        }
    }

    /// <summary>Média móvel de <see cref="SmoothBins"/> bins, no lugar.</summary>
    private static void Smooth(double[] values, int length, double[] scratch)
    {
        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += values[i];
            if (i >= SmoothBins) sum -= values[i - SmoothBins];
            scratch[i] = sum / Math.Min(i + 1, SmoothBins);
        }
        Array.Copy(scratch, values, length);
    }
}
