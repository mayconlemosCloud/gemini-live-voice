using System.IO;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>Onde o aplicativo guarda preferências, logs, gravações e transcrições.</summary>
/// <remarks>
/// Único lugar do sistema que decide caminhos. O núcleo não conhece disco: ele pede artefatos à
/// plataforma passando apenas um carimbo de tempo, e é aqui que isso vira um caminho real.
/// </remarks>
public static class AppPaths
{
    private const string AppFolderName = "GeminiTranslateV2";

    /// <summary>Pasta raiz em <c>%AppData%</c>, criada se não existir.</summary>
    public static string Root => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName));

    /// <summary>Pasta de logs, gravações e transcrições da sessão.</summary>
    public static string Logs => Ensure(Path.Combine(Root, "logs"));

    /// <summary>Arquivo de preferências.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Caminho de um artefato da sessão dentro da pasta de logs.</summary>
    public static string InLogs(string fileName) => Path.Combine(Logs, fileName);

    private static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
