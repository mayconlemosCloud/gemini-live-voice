using System.IO;
using System.Text;

namespace GeminiTranslateV2;

/// <summary>
/// One session transcript file with both directions interleaved: what THEY said and the
/// translation you heard, what YOU said and the translation they heard. Transcription text
/// streams in as word fragments, so each stream is buffered and written as one line when it
/// goes quiet for a moment (or on Dispose); the line's timestamp is when its first fragment
/// arrived — lines can land slightly out of order in the file, the timestamps disambiguate.
/// </summary>
public sealed class TranscriptLog : IDisposable
{
    private const int FlushAfterMs = 2500;

    private sealed class Pending
    {
        public readonly StringBuilder Text = new();
        public DateTime StartedAt;
        public DateTime LastAt;
    }

    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, Pending> _streams = new();
    private bool _disposed;

    public string Path { get; }

    public TranscriptLog(string path)
    {
        Path = path;
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        Log.Write("Transcript", $"transcrição da conversa em: {path}");
    }

    public void Append(string label, string fragment)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTime.Now;
            foreach (var (l, s) in _streams)
                if (s.Text.Length > 0 && (now - s.LastAt).TotalMilliseconds > FlushAfterMs)
                    WriteLine(l, s);

            if (!_streams.TryGetValue(label, out var mine))
                _streams[label] = mine = new Pending();
            if (mine.Text.Length == 0) mine.StartedAt = now;
            mine.Text.Append(fragment);
            mine.LastAt = now;
        }
    }

    private void WriteLine(string label, Pending s)
    {
        try { _writer.WriteLine($"{s.StartedAt:HH:mm:ss} {label,-16} {s.Text.ToString().Trim()}"); }
        catch { }
        s.Text.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (label, s) in _streams)
                if (s.Text.Length > 0) WriteLine(label, s);
            try { _writer.Dispose(); } catch { }
        }
    }
}
