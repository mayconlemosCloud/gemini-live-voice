using System.Diagnostics;

namespace GeminiTranslate.Core.Signal;

/// <summary>Onde está a sua fala e onde está a tradução dela, na mesma linha do tempo.</summary>
/// <param name="SpokenMs">Milissegundos de FALA — não de relógio — produzidos nesta fala.</param>
/// <param name="PlayedMs">Milissegundos de tradução que já saíram no alto-falante.</param>
/// <param name="PendingMs">Estimativa do que ainda falta sair, em milissegundos de saída.</param>
/// <param name="GapMs">
/// Distância entre as duas cabeças: o que já foi dito e ainda não saiu. É o que a barra desenha.
/// NÃO é preenchido pelo <see cref="SpeechBalance"/> — quem preenche é a direção de tradução, a
/// partir do atraso medido pelo <see cref="LatencyProbe"/>. NaN significa "ainda medindo".
/// </param>
/// <param name="SinceSpeechMs">Há quanto tempo não chega fala, para a distância decair.</param>
/// <param name="Active">Falso quando a fala terminou e tudo já saiu.</param>
public readonly record struct BalanceSnapshot(
    double SpokenMs,
    double PlayedMs,
    double PendingMs,
    double GapMs,
    double SinceSpeechMs,
    bool Active);

/// <summary>
/// Acompanha o SALDO de uma fala: quanto de áudio falado entrou e quanto de tradução já saiu.
/// </summary>
/// <remarks>
/// Reinicia a cada fala. Depois de uma pausa em que tudo já saiu os contadores voltam a zero,
/// porque o número útil no meio de uma reunião é "faltam 2 s desta frase", não "falei 612 s hoje".
///
/// POR QUE NÃO DÁ PARA COMPARAR OS DOIS DIRETO: a tradução não tem a mesma duração do original.
/// Inglês costuma sair mais curto que português, então "falei 12 s, saiu 9 s" tanto pode ser 3 s
/// pendentes quanto o idioma tendo comprimido. Por isso a razão entre os dois é APRENDIDA ao
/// longo da sessão, sobre as falas já concluídas — nas quais, por definição, tudo o que era para
/// sair saiu — e é ela que converte fala falada em fala esperada na saída. Enquanto não há
/// amostra suficiente, assume 1:1, que é o palpite menos errado e se corrige em poucas falas.
/// </remarks>
public sealed class SpeechBalance
{
    /// <summary>Mesmo limiar do <see cref="LatencyProbe"/>, para as duas medidas concordarem.</summary>
    private const float SpeechRms = 0.02f;

    /// <summary>Pausa na fala que autoriza fechar a fala atual.</summary>
    private const double TurnGapMs = 1200;

    /// <summary>O dub também precisa ter parado de chegar, senão zeraria no meio da saída.</summary>
    private const double DubIdleMs = 800;

    /// <summary>Fila de reprodução considerada vazia. Abaixo disso não há mais nada para sair.</summary>
    private const double DrainedMs = 120;

    /// <summary>Fala acumulada mínima antes de confiar na razão aprendida.</summary>
    private const double RatioWarmupMs = 5000;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _spokenMs;
    private double _dubbedMs;
    private double _lastSpokeAtMs = double.NegativeInfinity;
    private double _lastDubAtMs = double.NegativeInfinity;
    private double _totalSpokenMs;
    private double _totalDubbedMs;

    /// <summary>Quanto de saída este par de idiomas rende por segundo de entrada.</summary>
    private double Ratio => _totalSpokenMs > RatioWarmupMs
        ? Math.Clamp(_totalDubbedMs / _totalSpokenMs, 0.5, 2.0)
        : 1.0;

    /// <summary>Chunk de 16 kHz mono PCM16 que acabou de ser enviado. Silêncio não conta como fala.</summary>
    public void Spoke(byte[] pcm)
    {
        if (Pcm.Rms(pcm) < SpeechRms) return;

        lock (_gate)
        {
            _spokenMs += Pcm.DurationMs(pcm, AudioRates.Wire);
            _lastSpokeAtMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Chunk de tradução de 24 kHz mono PCM16 que acabou de chegar.</summary>
    public void Heard(byte[] pcm)
    {
        if (Pcm.Rms(pcm) < SpeechRms) return;

        lock (_gate)
        {
            _dubbedMs += Pcm.DurationMs(pcm, AudioRates.Dub);
            _lastDubAtMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// Estado atual.
    /// </summary>
    /// <param name="playoutMs">
    /// Fila de reprodução: o dub CHEGOU, mas essa parte ainda não saiu no alto-falante, e é
    /// justamente a diferença que o usuário quer ver.
    /// </param>
    public BalanceSnapshot Read(double playoutMs)
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (IsTurnFinished(now, playoutMs)) CloseTurn();

            if (_spokenMs <= 0) return new BalanceSnapshot(0, 0, 0, 0, 0, false);

            double expected = _spokenMs * Ratio;
            double played = Math.Max(0, _dubbedMs - playoutMs);
            double pending = Math.Max(0, expected - played);

            return new BalanceSnapshot(_spokenMs, played, pending, double.NaN,
                now - _lastSpokeAtMs, true);
        }
    }

    /// <summary>
    /// Parou de falar, parou de chegar dub, e a fila esvaziou. As três condições juntas: só a
    /// pausa na fala zeraria os contadores com áudio ainda saindo.
    /// </summary>
    private bool IsTurnFinished(double now, double playoutMs) =>
        _spokenMs > 0
        && now - _lastSpokeAtMs > TurnGapMs
        && now - _lastDubAtMs > DubIdleMs
        && playoutMs < DrainedMs;

    /// <summary>
    /// A fala terminada vira amostra para a razão aprendida: aqui tudo o que era para sair saiu.
    /// </summary>
    private void CloseTurn()
    {
        _totalSpokenMs += _spokenMs;
        _totalDubbedMs += _dubbedMs;
        _spokenMs = 0;
        _dubbedMs = 0;
    }

    /// <summary>Totais da sessão, para o log ao parar.</summary>
    public (double SpokenMs, double DubbedMs) Totals()
    {
        lock (_gate) return (_totalSpokenMs + _spokenMs, _totalDubbedMs + _dubbedMs);
    }
}
