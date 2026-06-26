using NAudio.Wave;

namespace GeminiLiveTranslate.Audio;

/// <summary>Down-mixes any number of channels to mono by averaging.</summary>
public sealed class ToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public ToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_scratch.Length < needed)
            _scratch = new float[needed];

        int read = _source.Read(_scratch, 0, needed);
        int frames = read / _channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < _channels; c++)
                sum += _scratch[i * _channels + c];
            buffer[offset + i] = sum / _channels;
        }
        return frames;
    }
}

/// <summary>Up-mixes a mono source to N channels by copying the sample to every channel.</summary>
public sealed class MonoToMultiSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public MonoToMultiSampleProvider(ISampleProvider source, int channels)
    {
        _source = source;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        if (_scratch.Length < frames)
            _scratch = new float[frames];

        int read = _source.Read(_scratch, 0, frames);
        for (int i = 0; i < read; i++)
        {
            float sample = _scratch[i];
            for (int c = 0; c < _channels; c++)
                buffer[offset + i * _channels + c] = sample;
        }
        return read * _channels;
    }
}
