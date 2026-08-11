using System.IO;
using System.Text;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>
/// Arquivo de transcrição da sessão com as duas direções intercaladas: o que ELES disseram e a
/// tradução que você ouviu, o que VOCÊ disse e a tradução que eles ouviram.
/// </summary>
/// <remarks>
/// A transcrição chega em fragmentos de palavra, então cada fluxo é acumulado e escrito como uma
/// linha quando fica quieto por um momento, ou no descarte. O horário da linha é o da chegada do
/// primeiro fragmento — linhas podem cair levemente fora de ordem no arquivo, e é o horário que
/// desambigua.
/// </remarks>
public sealed class TranscriptLog : ITranscriptSink
{
    private const int FlushAfterMs = 2500;

    private sealed class PendingLine
    {
        public readonly StringBuilder Text = new();
        public DateTime StartedAt;
        public DateTime LastAt;
    }

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, PendingLine> _streams = [];
    private bool _disposed;

    /// <param name="stamp">Carimbo de tempo que identifica os arquivos da sessão.</param>
    public TranscriptLog(string stamp)
    {
        var path = AppPaths.InLogs($"conversa-{stamp}.txt");
        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        Log.Write("Transcript", $"transcrição da conversa em: {path}");
    }

    /// <inheritdoc />
    public void Append(string label, string fragment)
    {
        lock (_gate)
        {
            if (_disposed) return;

            var now = DateTime.Now;
            FlushIdleStreams(now);

            if (!_streams.TryGetValue(label, out var pending))
                _streams[label] = pending = new PendingLine();

            if (pending.Text.Length == 0) pending.StartedAt = now;
            pending.Text.Append(fragment);
            pending.LastAt = now;
        }
    }

    /// <summary>Fecha as linhas de fluxos que ficaram quietos tempo suficiente.</summary>
    private void FlushIdleStreams(DateTime now)
    {
        foreach (var (label, pending) in _streams)
            if (pending.Text.Length > 0 && (now - pending.LastAt).TotalMilliseconds > FlushAfterMs)
                WriteLine(label, pending);
    }

    private void WriteLine(string label, PendingLine pending)
    {
        try
        {
            _writer.WriteLine($"{pending.StartedAt:HH:mm:ss} {label,-16} {pending.Text.ToString().Trim()}");
        }
        catch
        {
        }
        pending.Text.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var (label, pending) in _streams)
                if (pending.Text.Length > 0) WriteLine(label, pending);

            try { _writer.Dispose(); } catch { }
        }
    }
}
