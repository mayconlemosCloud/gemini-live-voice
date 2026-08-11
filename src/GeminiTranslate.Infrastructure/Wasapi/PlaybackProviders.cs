using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Repassa as amostras intactas, entregando cada bloco tocado ao observador corrente.
/// </summary>
/// <remarks>É por aqui que a gravação da conversa obtém exatamente o que o ouvinte ouviu.</remarks>
public sealed class TapSampleProvider(ISampleProvider source, Func<Action<float[], int, int>?> tap)
    : ISampleProvider
{
    /// <inheritdoc />
    public WaveFormat WaveFormat => source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int got = source.Read(buffer, offset, count);
        if (got > 0) tap()?.Invoke(buffer, offset, got);
        return got;
    }
}

/// <summary>
/// Segura a reprodução até acumular um mínimo no buffer de origem, evitando engasgo no primeiro
/// bloco de cada rajada.
/// </summary>
public sealed class PrerollProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BufferedWaveProvider _queue;
    private readonly int _prerollMs;
    private bool _playing;

    /// <param name="source">Fluxo a ser tocado.</param>
    /// <param name="queue">Buffer que alimenta <paramref name="source"/> e cuja ocupação é medida.</param>
    /// <param name="prerollMs">Quanto precisa estar acumulado antes de começar a tocar.</param>
    public PrerollProvider(ISampleProvider source, BufferedWaveProvider queue, int prerollMs)
    {
        _source = source;
        _queue = queue;
        _prerollMs = prerollMs;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        double buffered = _queue.BufferedDuration.TotalMilliseconds;
        if (!_playing && buffered >= _prerollMs) _playing = true;

        if (!_playing)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int got = _source.Read(buffer, offset, count);
        if (buffered <= 1.0) _playing = false;
        return got;
    }
}
