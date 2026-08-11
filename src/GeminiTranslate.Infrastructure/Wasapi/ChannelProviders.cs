using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>Base das reduções multicanal → mono: lê quadros inteiros e delega a combinação.</summary>
public abstract class ChannelReducerProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _frames = [];

    /// <param name="source">Fluxo multicanal de origem.</param>
    protected ChannelReducerProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <summary>Reduz um quadro a uma amostra. <paramref name="offset"/> aponta o canal 0 do quadro.</summary>
    protected abstract float Reduce(float[] frames, int offset, int channels);

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_frames.Length < needed) _frames = new float[needed];

        int got = _source.Read(_frames, 0, needed);
        int frames = got / _channels;
        for (int f = 0; f < frames; f++)
            buffer[offset + f] = Reduce(_frames, f * _channels, _channels);
        return frames;
    }
}

/// <summary>Extrai o canal 0, sem mistura e portanto sem interação de fase entre canais.</summary>
public sealed class FirstChannelProvider(ISampleProvider source) : ChannelReducerProvider(source)
{
    /// <inheritdoc />
    protected override float Reduce(float[] frames, int offset, int channels) => frames[offset];
}

/// <summary>Faz a média de qualquer número de canais, reduzindo a mono.</summary>
public sealed class DownmixToMonoProvider(ISampleProvider source) : ChannelReducerProvider(source)
{
    /// <inheritdoc />
    protected override float Reduce(float[] frames, int offset, int channels)
    {
        float sum = 0;
        for (int c = 0; c < channels; c++) sum += frames[offset + c];
        return sum / channels;
    }
}

/// <summary>Replica um fluxo mono em N canais idênticos, para dispositivos com mais de dois.</summary>
public sealed class MonoToChannelsProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _mono = [];

    /// <param name="source">Fluxo mono de origem.</param>
    /// <param name="channels">Número de canais desejado na saída.</param>
    public MonoToChannelsProvider(ISampleProvider source, int channels)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int frames = count / channels;
        if (_mono.Length < frames) _mono = new float[frames];

        int got = _source.Read(_mono, 0, frames);
        for (int f = 0; f < got; f++)
            for (int c = 0; c < channels; c++)
                buffer[offset + f * channels + c] = _mono[f];
        return got * channels;
    }
}
