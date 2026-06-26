using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace GeminiLiveTranslate.Logging;

/// <summary>
/// Thread-safe logger that writes every line to a per-session file in
/// %AppData%\GeminiLiveTranslate\logs and also raises <see cref="LineWritten"/>
/// so the UI can show a live tail. Built to capture as much detail as possible
/// for diagnosing the audio pipeline and the Live API WebSocket protocol.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static readonly Stopwatch _clock = Stopwatch.StartNew();

    public static string? FilePath { get; private set; }
    public static string LogFolder { get; private set; } = "";

    /// <summary>Raised for every line written (already formatted). Marshalling to a UI thread is the subscriber's job.</summary>
    public static event Action<string>? LineWritten;

    public static void Init()
    {
        try
        {
            LogFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GeminiLiveTranslate", "logs");
            Directory.CreateDirectory(LogFolder);
            FilePath = Path.Combine(LogFolder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch
        {
            _writer = null;
        }

        Info("App", $"=== Tradutor de Reuniões — Gemini 3.5 Live ===");
        Info("App", $"Log: {FilePath}");
        Info("App", $"OS: {Environment.OSVersion}  | .NET: {Environment.Version}  | 64-bit: {Environment.Is64BitProcess}");
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Debug(string category, string message) => Write("DBG ", category, message);

    public static void Error(string category, string message, Exception? ex = null)
    {
        if (ex is null) Write("ERR ", category, message);
        else Write("ERR ", category, $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex}");
    }

    private static void Write(string level, string category, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{_clock.ElapsedMilliseconds,8}] {level} [{category}] {message}";
        lock (_lock)
        {
            try { _writer?.WriteLine(line); } catch { }
        }
        try { LineWritten?.Invoke(line); } catch { }
    }

    /// <summary>Masks an API key, showing only the last 4 characters.</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(vazio)";
        return key.Length <= 4 ? "****" : $"****{key[^4..]} (len {key.Length})";
    }

    /// <summary>
    /// Returns a compact, log-safe version of a JSON message where any long string
    /// (e.g. base64 audio) is replaced by "&lt;N chars&gt;" so logs stay readable.
    /// </summary>
    public static string Summarize(string json, int maxStringLen = 120)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return json;
            Shorten(node, maxStringLen);
            return node.ToJsonString();
        }
        catch
        {
            return json.Length > 400 ? json[..400] + $"…(+{json.Length - 400})" : json;
        }
    }

    private static void Shorten(JsonNode node, int maxLen)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > maxLen)
                        obj[kv.Key] = $"<{s.Length} chars>";
                    else if (kv.Value is not null)
                        Shorten(kv.Value, maxLen);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue av && av.TryGetValue<string>(out var s) && s.Length > maxLen)
                        arr[i] = $"<{s.Length} chars>";
                    else if (arr[i] is not null)
                        Shorten(arr[i]!, maxLen);
                }
                break;
        }
    }

    public static void OpenFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(LogFolder))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogFolder}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
