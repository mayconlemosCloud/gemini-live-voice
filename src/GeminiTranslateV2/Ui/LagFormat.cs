using System.Windows.Media;
using GeminiTranslate.Core.Session;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Como o atraso é escrito na tela. Fica num lugar só porque aparece em dois — a barra da janela
/// principal e a etiqueta flutuante — que precisam concordar.
/// </summary>
internal static class LagFormat
{
    /// <summary>
    /// A partir daqui a medição é de outro trecho da conversa.
    /// </summary>
    /// <remarks>
    /// O número CONTINUA na tela, apagado: sumir com ele deixaria o indicador vazio exatamente
    /// numa pausa, que é quando se olha para ele. Cinza quer dizer "foi isto da última vez", e
    /// não "não sei".
    /// </remarks>
    private const double StaleMs = 25_000;

    private const double GoodMs = 2500;
    private const double WarnMs = 4000;

    /// <summary>Cor de um indicador sem medição ainda.</summary>
    public static readonly Brush Idle = Frozen(0x9A, 0xA0, 0xA6);

    private static readonly Brush Stale = Frozen(0x6B, 0x6E, 0x73);
    private static readonly Brush Good = Frozen(0x6E, 0xC2, 0x7A);
    private static readonly Brush Warn = Frozen(0xE0, 0xB0, 0x4A);
    private static readonly Brush Bad = Frozen(0xE0, 0x6C, 0x6C);

    /// <summary>Texto e cor do indicador de atraso de uma direção.</summary>
    /// <param name="name">Rótulo curto exibido antes do número.</param>
    /// <param name="direction">Direção medida.</param>
    public static (string Text, Brush Brush) Describe(string name, TranslationDirection direction)
    {
        var (lagMs, ageMs) = direction.Lag;
        string speeding = direction.CatchUpSpeed > 1.01 ? " ⏩" : "";

        if (double.IsNaN(lagMs)) return ($"{name} —{speeding}", Idle);

        string text = $"{name} {lagMs / 1000:0.0} s{speeding}";
        if (ageMs > StaleMs) return (text, Stale);

        return (text, lagMs < GoodMs ? Good : lagMs < WarnMs ? Warn : Bad);
    }

    /// <summary>Pincel congelado, seguro para uso entre threads e mais barato de desenhar.</summary>
    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
