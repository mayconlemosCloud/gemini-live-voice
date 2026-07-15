using System.IO;
using System.Text.Json;

namespace GeminiTranslateSdk;

public sealed record Language(string Code, string Name)
{
    public override string ToString() => Name;
}

public static class Languages
{
    public static readonly IReadOnlyList<Language> All = new[]
    {
        new Language("pt", "Português"),
        new Language("en", "Inglês (English)"),
        new Language("es", "Espanhol (Español)"),
        new Language("fr", "Francês (Français)"),
        new Language("de", "Alemão (Deutsch)"),
        new Language("it", "Italiano"),
        new Language("ja", "Japonês (日本語)"),
        new Language("ko", "Coreano (한국어)"),
        new Language("zh-Hans", "Chinês simplificado (中文)"),
        new Language("ru", "Russo (Русский)"),
        new Language("hi", "Hindi (हिन्दी)"),
        new Language("ar", "Árabe (العربية)"),
    };

    public static Language ByCode(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
}

/// <summary>Persisted in %AppData%\GeminiTranslateSdk\settings.json.</summary>
public sealed class Settings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-live-translate-preview";

    public string? MeetingDeviceId { get; set; }    // render endpoint the meeting plays to (loopback-captured)
    public string? HeadphonesDeviceId { get; set; } // render endpoint where I hear the incoming translation
    public string? MicDeviceId { get; set; }        // my real microphone
    public string? VirtualMicDeviceId { get; set; } // render side of the cable the meeting uses as mic

    public string MyLang { get; set; } = "pt";      // what I want to hear
    public string TheirLang { get; set; } = "en";   // what they want to hear

    /// <summary>Volume (0..1) of the ORIGINAL voice mixed under the translation, both directions.</summary>
    public double OriginalVolume { get; set; } = 0.20;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GeminiTranslateSdk");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
