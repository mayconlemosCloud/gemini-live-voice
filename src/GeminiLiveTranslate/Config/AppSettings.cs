using System.IO;
using System.Text.Json;

namespace GeminiLiveTranslate.Config;

/// <summary>User configuration, persisted to %AppData%\GeminiLiveTranslate\settings.json.</summary>
public sealed class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-live-translate-preview";

    // --- Incoming direction: the other person -> you ---
    /// <summary>Render endpoint where the meeting plays; captured via loopback.</summary>
    public string? MeetingOutputDeviceId { get; set; }
    /// <summary>Render endpoint where the translated audio you hear is played.</summary>
    public string? HeadphonesDeviceId { get; set; }
    /// <summary>Language you want to understand in (BCP-47).</summary>
    public string IncomingTargetLang { get; set; } = "pt";

    // --- Outgoing direction: you -> the other person ---
    /// <summary>Your real microphone (capture endpoint).</summary>
    public string? MicDeviceId { get; set; }
    /// <summary>Virtual cable input (render endpoint) the meeting app uses as its mic.</summary>
    public string? VirtualMicDeviceId { get; set; }
    /// <summary>Language the other person should hear (BCP-47).</summary>
    public string OutgoingTargetLang { get; set; } = "en";

    public bool EnableOutgoing { get; set; } = true;

    /// <summary>Stream all audio continuously (best prosody, matches Google's example). If false, applies a client-side silence gate.</summary>
    public bool ContinuousStreaming { get; set; } = true;

    /// <summary>Play the original audio (ducked) under the translation, like Google Meet. Also routes the original through the app, which prevents capture feedback.</summary>
    public bool DuckOriginal { get; set; } = true;

    /// <summary>Original volume (0..1) while the translation is speaking. Lower = translation stands out more. Default 0.18 ≈ 18%.</summary>
    public double DuckOriginalLevel { get; set; } = 0.18;

    /// <summary>
    /// When true, on Start the app sets the virtual cable as the Windows default communication
    /// device(s) so meeting apps (WhatsApp, Teams…) pick the right route without manual tweaking,
    /// and restores the previous defaults on Stop. Browser-based Meet still chooses per-tab.
    /// </summary>
    public bool AutoSetDefaultDevice { get; set; } = false;

    // --- Cost estimate (the Gemini API has no real-time spend endpoint; we estimate from tokens) ---
    /// <summary>Your spending cap, in BRL. 0 = no limit set.</summary>
    public double BudgetBrl { get; set; } = 0;
    /// <summary>Estimated USD price per 1M input (audio) tokens — editable, since the preview price isn't fully public.</summary>
    public double InputUsdPerMillion { get; set; } = 3.0;
    /// <summary>Estimated USD price per 1M output (audio) tokens.</summary>
    public double OutputUsdPerMillion { get; set; } = 12.0;
    /// <summary>Last known USD→BRL rate (auto-refreshed when online).</summary>
    public double UsdToBrl { get; set; } = 5.20;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GeminiLiveTranslate");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-fatal */ }
    }
}
