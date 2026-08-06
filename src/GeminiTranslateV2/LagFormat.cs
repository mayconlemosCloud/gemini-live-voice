using System.Windows.Media;

namespace GeminiTranslateV2;

/// <summary>
/// Como o atraso é escrito na tela. Fica num lugar só porque aparece em dois: na barra da janela
/// principal e na etiqueta flutuante (<see cref="LagWindow"/>), que precisam concordar.
/// </summary>
internal static class LagFormat
{
    /// <summary>
    /// A partir daqui a medição é de outro trecho da conversa. O número CONTINUA na tela, apagado —
    /// sumir com ele deixaria o indicador vazio exatamente numa pausa, que é quando se olha para
    /// ele. Cinza quer dizer "foi isto da última vez", não "não sei".
    /// </summary>
    private const double StaleMs = 25_000;

    public static readonly Brush Idle = Frozen(0x9A, 0xA0, 0xA6);
    private static readonly Brush Stale = Frozen(0x6B, 0x6E, 0x73);
    private static readonly Brush Good = Frozen(0x6E, 0xC2, 0x7A);
    private static readonly Brush Warn = Frozen(0xE0, 0xB0, 0x4A);
    private static readonly Brush Bad = Frozen(0xE0, 0x6C, 0x6C);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static (string Text, Brush Brush) Describe(string name, Direction d)
    {
        var (lagMs, ageMs) = d.Lag;
        string fast = d.CatchUpSpeed > 1.01 ? " ⏩" : "";

        if (double.IsNaN(lagMs)) return ($"{name} —{fast}", Idle);

        string text = $"{name} {lagMs / 1000:0.0} s{fast}";
        if (ageMs > StaleMs) return (text, Stale);

        return (text, lagMs < 2500 ? Good : lagMs < 4000 ? Warn : Bad);
    }
}
