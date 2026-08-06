using System.Diagnostics;

namespace GeminiTranslateV2;

/// <summary>Onde está a sua fala e onde está a tradução dela, na mesma linha do tempo.</summary>
/// <param name="SpokenMs">Segundos de FALA (não de relógio) produzidos nesta fala.</param>
/// <param name="PlayedMs">Segundos de tradução que já saíram no alto-falante.</param>
/// <param name="PendingMs">Estimativa do que ainda falta sair, em segundos de SAÍDA.</param>
/// <param name="GapMs">
/// Distância entre as duas cabeças, em segundos: o que você já disse e ainda não saiu. É o que a
/// barra desenha. NÃO é preenchido aqui — quem preenche é <see cref="Direction"/>, a partir do
/// atraso medido pelo <see cref="LatencyProbe"/>. NaN = ainda medindo.
/// </param>
/// <param name="SinceSpeechMs">Há quanto tempo não chega fala. Usado para a distância decair.</param>
/// <param name="Active">Falso quando a fala terminou e tudo já saiu (contadores zerados).</param>
public readonly record struct BalanceSnapshot(
    double SpokenMs, double PlayedMs, double PendingMs, double GapMs, double SinceSpeechMs, bool Active);

/// <summary>
/// Acompanha o SALDO de uma fala: quanto de áudio falado entrou e quanto de tradução já saiu.
/// Reinicia a cada fala — depois de uma pausa em que tudo já saiu, os contadores voltam a zero,
/// porque o número útil no meio de uma reunião é "faltam 2 s desta frase", não "falei 612 s hoje".
///
/// PORQUE NÃO DÁ PARA COMPARAR OS DOIS DIRETO: a tradução não tem a mesma duração do original.
/// Inglês costuma sair mais curto que português, então "falei 12 s, saiu 9 s" tanto pode ser 3 s
/// pendentes quanto o idioma tendo comprimido. Por isso a razão entre os dois é APRENDIDA ao longo
/// da sessão, sobre as falas já concluídas (em que, por definição, tudo o que era para sair saiu),
/// e é ela que converte fala falada em fala esperada na saída. Enquanto não há amostra suficiente,
/// assume 1:1 — que é o palpite menos errado e se corrige sozinho em poucas falas.
/// </summary>
public sealed class SpeechBalance
{
    private const float SpeechRms = 0.02f;          // mesmo limiar do LatencyProbe
    private const int SentRate = Resample16k.Rate;  // taxa do que Spoke recebe
    private const int DubRate = 24000;              // taxa do que Heard recebe

    /// <summary>Pausa na fala que autoriza fechar a fala atual.</summary>
    private const double TurnGapMs = 1200;

    /// <summary>E o dub também precisa ter parado de chegar — senão zeraria no meio da saída.</summary>
    private const double DubIdleMs = 800;

    /// <summary>Fila de reprodução considerada vazia. Abaixo disso não há mais nada para sair.</summary>
    private const double DrainedMs = 120;

    /// <summary>Fala acumulada mínima antes de confiar na razão aprendida.</summary>
    private const double RatioWarmupMs = 5000;

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _spokenMs, _dubbedMs;
    private double _lastSpokeAt = double.NegativeInfinity;
    private double _lastDubAt = double.NegativeInfinity;

    private double _totalSpokenMs, _totalDubbedMs;

    /// <summary>Quanto de saída este par de idiomas rende por segundo de entrada.</summary>
    private double Ratio => _totalSpokenMs > RatioWarmupMs
        ? Math.Clamp(_totalDubbedMs / _totalSpokenMs, 0.5, 2.0)
        : 1.0;

    /// <summary>Chunk que ACABOU de ser enviado (16 kHz mono PCM16).</summary>
    public void Spoke(byte[] pcm)
    {
        if (Rms(pcm) < SpeechRms) return;       // silêncio não é fala; contar relógio não serve
        lock (_lock)
        {
            _spokenMs += pcm.Length / 2.0 * 1000 / SentRate;
            _lastSpokeAt = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Chunk de tradução que ACABOU de chegar (24 kHz mono PCM16).</summary>
    public void Heard(byte[] pcm)
    {
        if (Rms(pcm) < SpeechRms) return;
        lock (_lock)
        {
            _dubbedMs += pcm.Length / 2.0 * 1000 / DubRate;
            _lastDubAt = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Estado atual. <paramref name="playoutMs"/> é a fila de reprodução: o dub CHEGOU, mas essa
    /// parte ainda não saiu no alto-falante, e é justamente a diferença que o usuário quer ver.
    /// </summary>
    public BalanceSnapshot Read(double playoutMs)
    {
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalMilliseconds;

            // Fim de fala: parou de falar, parou de chegar dub, e a fila esvaziou. As três
            // condições juntas — só a pausa na fala zeraria os contadores com áudio ainda saindo.
            if (_spokenMs > 0
                && now - _lastSpokeAt > TurnGapMs
                && now - _lastDubAt > DubIdleMs
                && playoutMs < DrainedMs)
            {
                // A fala terminada vira amostra para a razão: aqui tudo o que era para sair saiu.
                _totalSpokenMs += _spokenMs;
                _totalDubbedMs += _dubbedMs;
                _spokenMs = _dubbedMs = 0;
            }

            if (_spokenMs <= 0) return new BalanceSnapshot(0, 0, 0, 0, 0, false);

            double expected = _spokenMs * Ratio;
            double played = Math.Max(0, _dubbedMs - playoutMs);
            double pending = Math.Max(0, expected - played);
            // GapMs fica em NaN: quem sabe a distância é o LatencyProbe, e é Direction que junta as
            // duas coisas. Derivá-la daqui exigia que os dois lados fossem contados na mesma escala,
            // o que dependia do ganho, do limiar e da razão do par de idiomas — três coisas que
            // podiam sair do lugar em silêncio, e saíram (razão medida de 2,65).
            return new BalanceSnapshot(_spokenMs, played, pending, double.NaN,
                now - _lastSpokeAt, true);
        }
    }

    /// <summary>Totais da sessão, para o log ao parar.</summary>
    public (double SpokenMs, double DubbedMs) Totals()
    {
        lock (_lock) return (_totalSpokenMs + _spokenMs, _totalDubbedMs + _dubbedMs);
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
