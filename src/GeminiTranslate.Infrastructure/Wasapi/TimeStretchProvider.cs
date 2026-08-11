using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Signal;
using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Liga o WSOLA do núcleo ao encadeamento de amostras do NAudio.
/// </summary>
/// <remarks>
/// A matemática vive em <see cref="Wsola"/>, no núcleo, onde pode ser testada com um gerador de
/// onda em memória. Aqui há só a adaptação de interface — que é exatamente o motivo de a divisão
/// existir: o algoritmo não deve depender da biblioteca de áudio para ser verificável.
/// </remarks>
public sealed class TimeStretchProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Wsola _wsola;

    /// <param name="source">Fluxo mono a acelerar.</param>
    /// <param name="targetSpeed">Velocidade desejada, consultada a cada quadro.</param>
    public TimeStretchProvider(ISampleProvider source, Func<double> targetSpeed)
    {
        if (source.WaveFormat.Channels != 1)
            throw new ArgumentException("o time-stretch é mono", nameof(source));

        _source = source;
        _wsola = new Wsola(new SampleProviderReader(source), targetSpeed);
    }

    /// <summary>Velocidade em uso agora, já suavizada. 1,0 significa sem aceleração.</summary>
    public double Speed => _wsola.Speed;

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count) => _wsola.Read(buffer, offset, count);

    /// <summary>Expõe um <see cref="ISampleProvider"/> do NAudio como leitor do núcleo.</summary>
    private sealed class SampleProviderReader(ISampleProvider source) : ISampleReader
    {
        /// <inheritdoc />
        public int Read(float[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }
}
