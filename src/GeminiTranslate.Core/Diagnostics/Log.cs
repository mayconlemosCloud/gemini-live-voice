using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.Core.Diagnostics;

/// <summary>Registro da sessão atual, com hora e origem em cada linha.</summary>
/// <remarks>
/// Estático porque é chamado de toda parte, inclusive de threads de áudio em tempo real, onde
/// injetar um logger em cada objeto custaria mais ruído do que resolve. O DESTINO, porém, é
/// injetado: o núcleo formata a linha e não sabe se ela vai para arquivo, console ou lugar
/// nenhum — que é o que o mantém livre de dependência de disco e testável.
///
/// Toda falha de escrita é engolida: registrar nunca pode derrubar o app.
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();
    private static ILogSink? _sink;

    /// <summary>Define para onde as linhas vão. Chamado uma vez pela composição do aplicativo.</summary>
    public static void UseSink(ILogSink sink)
    {
        lock (Gate) _sink = sink;
    }

    /// <summary>Grava uma linha com hora e <paramref name="tag"/> de origem.</summary>
    public static void Write(string tag, string message)
    {
        lock (Gate)
        {
            try { _sink?.Write($"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}"); }
            catch { }
        }
    }
}
