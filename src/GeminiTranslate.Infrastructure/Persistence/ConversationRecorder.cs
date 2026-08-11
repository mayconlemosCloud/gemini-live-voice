using System.Diagnostics;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;
using GeminiTranslate.Infrastructure.Wasapi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GeminiTranslate.Infrastructure.Persistence;

/// <summary>
/// Grava a sessão inteira num ÚNICO WAV estéreo, na linha do tempo do relógio: canal esquerdo é
/// o que VOCÊ ouve, canal direito é o que ELES ouvem.
/// </summary>
/// <remarks>
/// Nada é remixado. Cada canal deriva as amostras exatas que a saída daquela direção renderizou,
/// então a gravação é idêntica ao áudio ao vivo — a voz original já faz parte de cada mix.
///
/// Um temporizador avança os dois canais pelo tempo real decorrido para que fiquem alinhados; a
/// leitura completa com silêncio quando um lado está momentaneamente quieto.
/// </remarks>
public sealed class ConversationRecorder : IConversationRecorder
{
    private const int Rate = AudioRates.Dub;
    private const int FlushIntervalMs = 100;
    private const int BytesPerStereoFrame = 4;

    private readonly WaveFileWriter _writer;
    private readonly ChannelBuffer _left;
    private readonly ChannelBuffer _right;
    private readonly System.Timers.Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private long _writtenFrames;
    private float[] _leftSamples = [];
    private float[] _rightSamples = [];
    private byte[] _interleaved = [];
    private volatile bool _disposed;

    /// <param name="stamp">Carimbo de tempo que identifica os arquivos da sessão.</param>
    /// <param name="incomingMixFormat">Formato do mix da direção "Entrada".</param>
    /// <param name="outgoingMixFormat">Formato do mix da direção "Saída".</param>
    public ConversationRecorder(string stamp, AudioFormat incomingMixFormat, AudioFormat outgoingMixFormat)
    {
        var path = AppPaths.InLogs($"conversa-{stamp}.wav");
        _writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
        _left = new ChannelBuffer(incomingMixFormat);
        _right = new ChannelBuffer(outgoingMixFormat);

        _timer = new System.Timers.Timer(FlushIntervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Flush();
        _timer.Start();

        Log.Write("Gravação", $"gravando a conversa (estéreo: esq=você ouve, dir=eles ouvem) em: {path}");
    }

    /// <inheritdoc />
    public void WriteIncoming(float[] buffer, int offset, int count)
    {
        if (!_disposed) _left.Add(buffer, offset, count);
    }

    /// <inheritdoc />
    public void WriteOutgoing(float[] buffer, int offset, int count)
    {
        if (!_disposed) _right.Add(buffer, offset, count);
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            WriteElapsed();
        }
    }

    /// <summary>
    /// Escreve os quadros estéreo até a posição atual do relógio. O chamador detém o lock.
    /// </summary>
    private void WriteElapsed()
    {
        long targetFrames = _clock.ElapsedMilliseconds * Rate / 1000;
        int needed = (int)(targetFrames - _writtenFrames);
        if (needed <= 0) return;

        if (_leftSamples.Length < needed)
        {
            _leftSamples = new float[needed];
            _rightSamples = new float[needed];
            _interleaved = new byte[needed * BytesPerStereoFrame];
        }

        _left.Read(_leftSamples, needed);
        _right.Read(_rightSamples, needed);

        int at = 0;
        for (int i = 0; i < needed; i++)
        {
            WriteSample(_interleaved, ref at, _leftSamples[i]);
            WriteSample(_interleaved, ref at, _rightSamples[i]);
        }

        _writer.Write(_interleaved, 0, at);
        _writtenFrames = targetFrames;
    }

    /// <summary>Escreve uma amostra PCM16, limitada — tradução e original podem somar além da escala.</summary>
    private static void WriteSample(byte[] target, ref int at, float sample)
    {
        short value = (short)(Math.Clamp(sample, -1f, 1f) * 32767f);
        target[at++] = (byte)(value & 0xFF);
        target[at++] = (byte)((value >> 8) & 0xFF);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _timer.Stop(); _timer.Dispose(); } catch { }

        lock (_gate)
        {
            try { WriteElapsed(); } catch { }
            try { _writer.Dispose(); } catch { }
        }

        Log.Write("Gravação", "conversa gravada e finalizada.");
    }

    /// <summary>
    /// Um canal: derivação do player, no formato de mix do dispositivo, reduzida a mono e
    /// reamostrada para a taxa do arquivo.
    /// </summary>
    private sealed class ChannelBuffer
    {
        private readonly BufferedWaveProvider _input;
        private readonly ISampleProvider _output;
        private readonly object _addGate = new();
        private byte[] _scratch = [];

        public ChannelBuffer(AudioFormat mixFormat)
        {
            _input = new BufferedWaveProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, mixFormat.Channels))
            {
                ReadFully = true,
                BufferDuration = TimeSpan.FromSeconds(30),
                DiscardOnBufferOverflow = true
            };

            ISampleProvider samples = _input.ToSampleProvider();
            if (mixFormat.Channels > 1) samples = new DownmixToMonoProvider(samples);
            if (samples.WaveFormat.SampleRate != Rate) samples = new WdlResamplingSampleProvider(samples, Rate);
            _output = samples;
        }

        public void Add(float[] buffer, int offset, int count)
        {
            lock (_addGate)
            {
                int bytes = count * sizeof(float);
                if (_scratch.Length < bytes) _scratch = new byte[bytes];

                Buffer.BlockCopy(buffer, offset * sizeof(float), _scratch, 0, bytes);
                _input.AddSamples(_scratch, 0, bytes);
            }
        }

        /// <summary>Preenche <paramref name="destination"/>, completando com silêncio quando vazio.</summary>
        public void Read(float[] destination, int count) => _output.Read(destination, 0, count);
    }
}
