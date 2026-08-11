using System.IO;
using System.Text.Json;
using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>
/// Leitura e gravação das <see cref="Settings"/> em
/// <c>%AppData%\GeminiTranslateV2\settings.json</c>.
/// </summary>
/// <remarks>
/// Falhas são engolidas: preferência corrompida vira preferência padrão, e não uma tela de erro
/// na abertura do app.
/// </remarks>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Modelos de assistente que já foram padrão e que ninguém escolheu de propósito. Quem tem um
    /// destes salvo está batendo no teto de 20 pedidos/dia sem saber, então a migração o leva
    /// junto; um valor fora desta lista é escolha do usuário e fica como está.
    /// </summary>
    private static readonly string[] SupersededAssistantModels = ["gemini-2.5-flash"];

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GeminiTranslateV2");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <inheritdoc />
    public Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Settings();

            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            if (MigrateAssistantModel(loaded)) Save(loaded);
            return loaded;
        }
        catch
        {
            return new Settings();
        }
    }

    /// <inheritdoc />
    public void Save(Settings settings)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Troca um modelo de assistente obsoleto pelo padrão atual. Retorna true quando alterou.
    /// </summary>
    private static bool MigrateAssistantModel(Settings settings)
    {
        if (!SupersededAssistantModels.Contains(settings.AssistantModel)) return false;

        Log.Write("Assistente",
            $"modelo '{settings.AssistantModel}' substituído por '{Settings.DefaultAssistantModel}' " +
            "(cota gratuita 25× maior).");
        settings.AssistantModel = Settings.DefaultAssistantModel;
        return true;
    }
}
