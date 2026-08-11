using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Acelera a reprodução sem mexer no pitch, por WSOLA (overlap-add com busca de similaridade).
/// </summary>
/// <remarks>
/// Existe porque a recuperação de fila anterior, feita por interpolação linear, subia o pitch na
/// mesma proporção da velocidade: a 1,15× a voz ficava ~15% mais aguda, destruindo exatamente a
/// entonação que o modelo tinha acabado de copiar da voz original. O WSOLA não reamostra nada —
/// repete ou pula PERÍODOS INTEIROS de fala, escolhendo o ponto de emenda por correlação
/// cruzada — então a frequência fundamental sai intacta e só o ritmo muda.
///
/// Em <see cref="Speed"/> igual a 1 a saída é o sinal de entrada, amostra por amostra: com
/// janela de Hann periódica e salto de metade do quadro as janelas somam exatamente 1 (condição
/// COLA), e a busca trava no mesmo deslocamento do quadro anterior por ser o de correlação
/// máxima. Não há custo em qualidade por deixar isto sempre no caminho do áudio, só os 21 ms de
/// atraso algorítmico do quadro.
///
/// TETO REAL DO GANHO: acelerar só recupera o que já está NA FILA de reprodução. Medido nos logs
/// desta aplicação, o servidor devolve o dub a 1,02× do tempo real (p90 1,10×), então a fila fica
/// entre 90 e 330 ms — e é isso, não mais que isso, que dá para recuperar. O atraso grande mora
/// antes, entre a fala e a chegada do primeiro áudio traduzido, e nada aqui o alcança.
/// </remarks>
public sealed class Wsola
{
    /// <summary>Quadro de análise e síntese: 21 ms a 24 kHz, cerca de dois períodos de voz grave.</summary>
    private const int Frame = 512;

    /// <summary>Salto de síntese — metade do quadro, a sobreposição que a janela de Hann exige.</summary>
    private const int Hop = Frame / 2;

    /// <summary>Busca de emenda: ±6,7 ms, mais que um período de pitch em qualquer voz falada.</summary>
    private const int Search = 160;

    /// <summary>Variação máxima de velocidade por quadro, para a mudança ser inaudível.</summary>
    private const double Slew = 0.01;

    private readonly ISampleReader _source;
    private readonly Func<double> _targetSpeed;
    private readonly float[] _window = new float[Frame];

    /// <summary>Segunda metade do último quadro sintetizado, já janelada, esperando o overlap-add.</summary>
    private readonly float[] _tail = new float[Hop];

    /// <summary>
    /// Continuação natural do trecho recém-consumido: é contra isto que os candidatos do próximo
    /// quadro são correlacionados, e é o que faz a emenda cair em fase com a onda anterior.
    /// </summary>
    private readonly float[] _natural = new float[Hop];

    private readonly float[] _ready = new float[Hop];
    private int _readyLength;
    private int _readyRead;

    private float[] _input = new float[Frame + Search * 2 + Hop * 8];
    private int _inputLength;

    /// <summary>Ponteiro de análise, fracionário: avança <see cref="Hop"/> × velocidade por quadro.</summary>
    private double _position;

    private double _speed = 1.0;
    private bool _primed;

    /// <summary>Velocidade em uso agora, já suavizada. 1,0 significa sem aceleração.</summary>
    public double Speed => _speed;

    /// <param name="source">Fluxo mono a acelerar.</param>
    /// <param name="targetSpeed">Velocidade desejada, consultada a cada quadro.</param>
    public Wsola(ISampleReader source, Func<double> targetSpeed)
    {
        _source = source;
        _targetSpeed = targetSpeed;
        for (int i = 0; i < Frame; i++)
            _window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / Frame)));
    }

    /// <summary>Preenche <paramref name="buffer"/> com áudio já acelerado.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_readyRead >= _readyLength)
            {
                SynthesizeFrame();
                _readyRead = 0;
            }

            int take = Math.Min(count - written, _readyLength - _readyRead);
            Array.Copy(_ready, _readyRead, buffer, offset + written, take);
            _readyRead += take;
            written += take;
        }
        return written;
    }

    /// <summary>Produz mais <see cref="Hop"/> amostras de saída.</summary>
    private void SynthesizeFrame()
    {
        ApproachTargetSpeed();

        int center = (int)_position;
        Fill(center + Search + Frame + 1);
        int best = FindSplicePoint(center);

        for (int i = 0; i < Hop; i++)
            _ready[i] = _tail[i] + _input[best + i] * _window[i];
        _readyLength = Hop;

        for (int i = 0; i < Hop; i++)
            _tail[i] = _input[best + Hop + i] * _window[Hop + i];

        Array.Copy(_input, best + Hop, _natural, 0, Hop);

        _position += Hop * _speed;
        Compact();
    }

    /// <summary>
    /// Move a velocidade em passos de <see cref="Slew"/>. Um salto seco de 1,0 para 1,12 é
    /// audível como um engasgo; uma rampa de cerca de 120 ms não é.
    /// </summary>
    private void ApproachTargetSpeed()
    {
        double target = Math.Clamp(_targetSpeed(), 1.0, 2.0);
        _speed += Math.Clamp(target - _speed, -Slew, Slew);
    }

    /// <summary>
    /// Deslocamento cuja região de sobreposição melhor casa com a continuação natural.
    /// </summary>
    /// <remarks>
    /// A correlação é normalizada pela energia do candidato. Sem normalizar, a busca prefere
    /// sempre o trecho mais ALTO em vez do mais parecido, e a emenda cai fora de fase no ataque
    /// das sílabas.
    /// </remarks>
    private int FindSplicePoint(int center)
    {
        if (!_primed)
        {
            _primed = true;
            return Math.Clamp(center, 0, Math.Max(0, _inputLength - Frame));
        }

        double bestScore = double.NegativeInfinity;
        int best = center;
        int low = Math.Max(0, center - Search);
        int high = Math.Min(_inputLength - Frame, center + Search);

        for (int candidate = low; candidate <= high; candidate++)
        {
            double dot = 0, energy = 1e-9;
            for (int i = 0; i < Hop; i++)
            {
                float sample = _input[candidate + i];
                dot += sample * _natural[i];
                energy += sample * sample;
            }

            double score = dot / Math.Sqrt(energy);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Garante <paramref name="need"/> amostras válidas no buffer de entrada.</summary>
    private void Fill(int need)
    {
        if (need > _input.Length) Array.Resize(ref _input, need + Hop * 8);

        while (_inputLength < need)
        {
            int got = _source.Read(_input, _inputLength, need - _inputLength);
            if (got <= 0)
            {
                Array.Clear(_input, _inputLength, need - _inputLength);
                _inputLength = need;
                return;
            }
            _inputLength += got;
        }
    }

    /// <summary>Descarta o que já passou, mantendo a janela de busca para trás.</summary>
    private void Compact()
    {
        int drop = (int)_position - Search - Hop;
        if (drop <= Hop * 4) return;

        Array.Copy(_input, drop, _input, 0, _inputLength - drop);
        _inputLength -= drop;
        _position -= drop;
    }
}
