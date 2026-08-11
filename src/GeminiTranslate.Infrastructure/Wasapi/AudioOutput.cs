using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Toca a TRADUÇÃO (24 kHz mono PCM16 vinda do modelo) em volume cheio, com a voz ORIGINAL
/// (taxa nativa da captura) misturada por baixo num volume baixo e ajustável.
/// </summary>
/// <remarks>
/// Os números de latência são mínimos de propósito: 20 ms de preroll e 30 ms de buffer WASAPI,
/// contra os 150/100 originais. Um protótipo que tocava cada chunk no instante em que chegava já
/// soava fluido, então aqui só resta uma guarda contra engasgo.
/// </remarks>
public sealed class AudioOutput : IAudioSink
{
    private const int PrerollMs = 20;
    private const int DeviceBufferMs = 30;
    private const double OriginalQueueLimitMs = 1000;

    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _translation;
    private readonly BufferedWaveProvider _original;
    private readonly VolumeSampleProvider _originalVolume;
    private readonly TimeStretchProvider? _catchUp;
    private volatile Action<float[], int, int>? _renderTap;
    private bool _disposed;

    /// <inheritdoc />
    public TimeSpan TranslationQueue => _translation.BufferedDuration;

    /// <inheritdoc />
    public double CatchUpSpeed => _catchUp?.Speed ?? 1.0;

    /// <inheritdoc />
    public AudioFormat MixFormat { get; }

    /// <inheritdoc />
    public Action<float[], int, int>? RenderTap
    {
        set => _renderTap = value;
    }

    /// <summary>Volume da voz original tocada por baixo da tradução, de 0 a 1.</summary>
    public float OriginalVolume
    {
        get => _originalVolume.Volume;
        set => _originalVolume.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <param name="device">Endpoint de reprodução.</param>
    /// <param name="originalRate">Taxa nativa da voz original que será misturada por baixo.</param>
    /// <param name="originalVolume">Volume inicial da voz original, de 0 a 1.</param>
    /// <param name="catchUp">Se a tradução pode acelerar para recuperar fila.</param>
    /// <param name="tag">Origem exibida no log.</param>
    public AudioOutput(MMDevice device, int originalRate, float originalVolume, bool catchUp, string tag)
    {
        _device = device;
        _translation = new BufferedWaveProvider(new WaveFormat(AudioRates.Dub, 16, 1))
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(60),
            DiscardOnBufferOverflow = true
        };
        _original = new BufferedWaveProvider(new WaveFormat(originalRate, 16, 1))
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };

        var mixFormat = device.AudioClient.MixFormat;

        ISampleProvider translation = _translation.ToSampleProvider();
        if (catchUp)
        {
            _catchUp = new TimeStretchProvider(translation, () => CatchUpPolicy.SpeedFor(TranslationQueue.TotalMilliseconds));
            translation = _catchUp;
        }

        var translationOut = ToDeviceFormat(new PrerollProvider(translation, _translation, PrerollMs), mixFormat);
        _originalVolume = new VolumeSampleProvider(
            ToDeviceFormat(new PrerollProvider(_original.ToSampleProvider(), _original, PrerollMs), mixFormat))
        {
            Volume = Math.Clamp(originalVolume, 0f, 1f)
        };

        var mix = new MixingSampleProvider([translationOut, _originalVolume]) { ReadFully = true };
        MixFormat = new AudioFormat(mix.WaveFormat.SampleRate, mix.WaveFormat.Channels);

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, DeviceBufferMs);
        _output.Init(new SampleToWaveProvider(new TapSampleProvider(mix, () => _renderTap)));

        Log.Write(tag, $"saída em '{device.FriendlyName}' ({mixFormat.SampleRate} Hz " +
                       $"{mixFormat.Channels} ch), original a {originalVolume:P0}.");
    }

    /// <summary>Adapta um fluxo mono à taxa e à contagem de canais do dispositivo.</summary>
    private static ISampleProvider ToDeviceFormat(ISampleProvider source, WaveFormat mix)
    {
        if (source.WaveFormat.SampleRate != mix.SampleRate)
            source = new WdlResamplingSampleProvider(source, mix.SampleRate);

        return mix.Channels switch
        {
            1 => source,
            2 => new MonoToStereoSampleProvider(source),
            _ => new MonoToChannelsProvider(source, mix.Channels)
        };
    }

    /// <inheritdoc />
    public void Start() => _output.Play();

    /// <inheritdoc />
    public void EnqueueTranslation(byte[] pcm) => _translation.AddSamples(pcm, 0, pcm.Length);

    /// <summary>
    /// Enfileira um chunk da voz original. A fila é descartada quando passa de um segundo: a voz
    /// original é referência do que está sendo dito AGORA, e atrasada não serve para nada.
    /// </summary>
    public void EnqueueOriginal(byte[] pcm)
    {
        if (_original.BufferedDuration.TotalMilliseconds > OriginalQueueLimitMs) _original.ClearBuffer();
        _original.AddSamples(pcm, 0, pcm.Length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
