using System.IO;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;
using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>
/// Grava, por direção, dois WAV de diagnóstico: exatamente o que foi enviado ao modelo e
/// exatamente o que ele devolveu.
/// </summary>
/// <remarks>
/// É a partir destes arquivos que se responde "o problema está no que mandamos ou no que veio" —
/// foi assim que se mediu o clipping no ataque das frases que arruinava a transcrição. Falha de
/// gravação nunca impede a tradução de rodar: o diagnóstico é opcional.
/// </remarks>
public sealed class DiagnosticRecorder : IDiagnosticRecorder
{
    private readonly object _gate = new();
    private readonly WaveFileWriter? _sent;
    private readonly WaveFileWriter? _received;
    private bool _disposed;

    /// <param name="name">Nome da direção, usado no nome dos arquivos.</param>
    public DiagnosticRecorder(string name)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _sent = new WaveFileWriter(
                AppPaths.InLogs($"{name}-enviado-{stamp}.wav"),
                new WaveFormat(AudioRates.Wire, 16, 1));
            _received = new WaveFileWriter(
                AppPaths.InLogs($"{name}-recebido-{stamp}.wav"),
                new WaveFormat(AudioRates.Dub, 16, 1));
        }
        catch (Exception ex)
        {
            Log.Write(name, "sem gravação de diagnóstico: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public void WriteSent(byte[] pcm) => Write(_sent, pcm);

    /// <inheritdoc />
    public void WriteReceived(byte[] pcm) => Write(_received, pcm);

    private void Write(WaveFileWriter? writer, byte[] pcm)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { writer?.Write(pcm, 0, pcm.Length); } catch { }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _sent?.Dispose(); } catch { }
            try { _received?.Dispose(); } catch { }
        }
    }
}
