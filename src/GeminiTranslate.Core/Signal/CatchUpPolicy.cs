namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Decide a velocidade de reprodução da tradução em função do que está na fila.
/// </summary>
/// <remarks>
/// Separado da reprodução porque é uma regra de produto — quando vale a pena acelerar e até
/// onde — e não mecânica de áudio. O <see cref="Wsola"/> ainda suaviza a chegada ao alvo,
/// então quem escuta não percebe a transição, só que a fila para de crescer.
/// </remarks>
public static class CatchUpPolicy
{
    /// <summary>
    /// Abaixo desta fila a reprodução é 1× exato.
    /// </summary>
    /// <remarks>
    /// Não é zero de propósito: a fila oscila naturalmente entre 90 e 330 ms (medido), e acelerar
    /// nessa faixa só a esvaziaria para depois faltar áudio — trocaria atraso por engasgo.
    /// </remarks>
    private const double FloorMs = 350;

    /// <summary>Fila a partir da qual já se acelera no máximo.</summary>
    private const double FullMs = 900;

    /// <summary>
    /// Teto de velocidade. 1,12× é o limite em que o WSOLA continua inaudível em fala; acima
    /// disso as emendas começam a aparecer como uma leve gagueira nas vogais longas.
    /// </summary>
    private const double MaxSpeed = 1.12;

    /// <summary>Velocidade desejada para uma fila de <paramref name="queueMs"/> milissegundos.</summary>
    public static double SpeedFor(double queueMs)
    {
        if (queueMs <= FloorMs) return 1.0;

        double ramp = Math.Min(1.0, (queueMs - FloorMs) / (FullMs - FloorMs));
        return 1.0 + ramp * (MaxSpeed - 1.0);
    }
}
