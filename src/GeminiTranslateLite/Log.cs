using System.IO;

namespace GeminiTranslateLite;

/// <summary>Tiny session file logger: %AppData%\GeminiTranslateLite\logs\session-*.log.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static StreamWriter? _writer;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GeminiTranslateLite", "logs");

    public static void Write(string tag, string message)
    {
        lock (Lock)
        {
            try
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(Folder);
                    var path = Path.Combine(Folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    _writer = new StreamWriter(path, append: false) { AutoFlush = true };
                }
                _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}");
            }
            catch { /* logging must never break the app */ }
        }
    }
}
