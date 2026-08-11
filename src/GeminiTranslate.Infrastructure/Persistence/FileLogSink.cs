using System.IO;
using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>Escreve o log da sessão em <c>%AppData%\GeminiTranslateV2\logs\session-*.log</c>.</summary>
/// <remarks>
/// Um arquivo por execução, com autoflush: um travamento não pode levar junto as últimas linhas,
/// que costumam ser exatamente as que explicam o travamento.
/// </remarks>
public sealed class FileLogSink : ILogSink
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    /// <inheritdoc />
    public void Write(string line)
    {
        lock (_gate)
        {
            _writer ??= Create();
            _writer.WriteLine(line);
        }
    }

    private static StreamWriter Create()
    {
        var path = AppPaths.InLogs($"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        return new StreamWriter(path, append: false) { AutoFlush = true };
    }
}
