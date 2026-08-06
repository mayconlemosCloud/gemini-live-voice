using NAudio.Wave;

namespace GeminiTranslateV2;

/// <summary>
/// Acelera a reprodução SEM mexer no pitch (WSOLA — overlap-add com busca de similaridade).
///
/// Isto existe porque o CatchUp antigo, que fazia a mesma coisa por interpolação linear, subia o
/// pitch na mesma proporção da velocidade: a 1,15× a voz ficava ~15% mais aguda, destruindo
/// exatamente a entonação que o modelo tinha acabado de copiar da voz original. WSOLA não
/// reamostra nada — ele repete/pula PERÍODOS INTEIROS de fala, escolhendo o ponto de emenda por
/// correlação cruzada, então a frequência fundamental sai intacta e só o ritmo muda.
///
/// Em <see cref="Speed"/> = 1 a saída é o sinal de ENTRADA, amostra por amostra: com janela de
/// Hann periódica e salto de metade do quadro, as janelas somam exatamente 1 (condição COLA), e a
/// busca trava no mesmo deslocamento do quadro anterior por ser o de correlação máxima. Ou seja,
/// não há custo em qualidade por deixar isto sempre no caminho do áudio — só os <see cref="Frame"/>
/// amostras (21 ms) de atraso algorítmico.
///
/// TETO REAL DO GANHO: acelerar só recupera o que já está NA FILA de reprodução. Medido nos logs
/// desta aplicação, o servidor devolve o dub a 1,02× do tempo real (p90 1,10×), então a fila fica
/// entre 90 e 330 ms — e é isso, não mais que isso, que dá para recuperar. O atraso grande mora
/// antes, entre a fala e a chegada do primeiro áudio traduzido, e nada aqui alcança aquilo.
/// </summary>
internal sealed class TimeStretch : ISampleProvider
{
    /// <summary>Quadro de análise/síntese: 21 ms a 24 kHz, ~2 períodos de uma voz masculina grave.</summary>
    private const int Frame = 512;

    /// <summary>Salto de síntese — metade do quadro, a sobreposição que a janela de Hann exige.</summary>
    private const int Hop = Frame / 2;

    /// <summary>Busca de emenda: ±6,7 ms, mais que um período de pitch em qualquer voz falada.</summary>
    private const int Search = 160;

    /// <summary>Variação máxima de velocidade por quadro (~10 ms), para a mudança ser inaudível.</summary>
    private const double Slew = 0.01;

    private readonly ISampleProvider _src;
    private readonly Func<double> _targetSpeed;
    private readonly float[] _win = new float[Frame];

    /// <summary>Segunda metade do último quadro sintetizado, já janelada, esperando o overlap-add.</summary>
    private readonly float[] _tail = new float[Hop];

    /// <summary>
    /// Continuação NATURAL do trecho que acabou de ser consumido: é contra isto que os candidatos
    /// do próximo quadro são correlacionados. É o que faz a emenda cair em fase com a onda anterior.
    /// </summary>
    private readonly float[] _natural = new float[Hop];

    private float[] _in = new float[Frame + Search * 2 + Hop * 8];
    private int _inLen;

    /// <summary>Ponteiro de análise (fracionário: avança <see cref="Hop"/> × velocidade por quadro).</summary>
    private double _pos;

    private readonly float[] _pending = new float[Hop];
    private int _pendingLen;
    private int _pendingRead;

    private double _speed = 1.0;
    private bool _primed;

    /// <summary>Velocidade em uso agora, já suavizada — 1,0 = sem aceleração. Para a UI.</summary>
    public double Speed => _speed;

    public TimeStretch(ISampleProvider src, Func<double> targetSpeed)
    {
        if (src.WaveFormat.Channels != 1)
            throw new ArgumentException("TimeStretch é mono", nameof(src));
        _src = src;
        _targetSpeed = targetSpeed;
        for (int i = 0; i < Frame; i++)
            _win[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / Frame))); // Hann periódica
    }

    public WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pendingRead >= _pendingLen)
            {
                SynthesizeFrame();
                _pendingRead = 0;
            }
            int take = Math.Min(count - written, _pendingLen - _pendingRead);
            Array.Copy(_pending, _pendingRead, buffer, offset + written, take);
            _pendingRead += take;
            written += take;
        }
        return written;
    }

    /// <summary>Produz mais <see cref="Hop"/> amostras de saída em <see cref="_pending"/>.</summary>
    private void SynthesizeFrame()
    {
        // A velocidade só se move em passos de Slew: um salto seco de 1,0 para 1,12 é audível como
        // um "engasgo", uma rampa de ~120 ms não é.
        double target = Math.Clamp(_targetSpeed(), 1.0, 2.0);
        _speed += Math.Clamp(target - _speed, -Slew, Slew);

        int need = (int)_pos + Search + Frame + 1;
        Fill(need);

        int center = (int)_pos;
        int best = center;

        if (_primed)
        {
            // Correlação cruzada normalizada contra a continuação natural, na região de
            // sobreposição. Sem normalizar, a busca prefere sempre o trecho mais ALTO em vez do
            // mais parecido, e a emenda cai fora de fase no ataque das sílabas.
            double bestScore = double.NegativeInfinity;
            int lo = Math.Max(0, center - Search);
            int hi = Math.Min(_inLen - Frame, center + Search);
            for (int c = lo; c <= hi; c++)
            {
                double dot = 0, energy = 1e-9;
                for (int i = 0; i < Hop; i++)
                {
                    float s = _in[c + i];
                    dot += s * _natural[i];
                    energy += s * s;
                }
                double score = dot / Math.Sqrt(energy);
                if (score > bestScore) { bestScore = score; best = c; }
            }
        }
        else
        {
            best = Math.Clamp(center, 0, Math.Max(0, _inLen - Frame));
            _primed = true;
        }

        // Overlap-add: primeira metade do quadro novo somada à cauda janelada do quadro anterior.
        for (int i = 0; i < Hop; i++)
            _pending[i] = _tail[i] + _in[best + i] * _win[i];
        _pendingLen = Hop;

        for (int i = 0; i < Hop; i++)
            _tail[i] = _in[best + Hop + i] * _win[Hop + i];

        // A referência do próximo quadro é o que viria DEPOIS deste trecho, não depois do ponteiro.
        Array.Copy(_in, best + Hop, _natural, 0, Hop);

        _pos += Hop * _speed;
        Compact();
    }

    /// <summary>Garante <paramref name="need"/> amostras válidas em <see cref="_in"/>.</summary>
    private void Fill(int need)
    {
        if (need > _in.Length) Array.Resize(ref _in, need + Hop * 8);
        while (_inLen < need)
        {
            int got = _src.Read(_in, _inLen, need - _inLen);
            if (got <= 0)
            {
                // A fonte é um BufferedWaveProvider com ReadFully, então isto não deve acontecer;
                // se acontecer, silêncio é melhor que repetir o quadro anterior em loop.
                Array.Clear(_in, _inLen, need - _inLen);
                _inLen = need;
                return;
            }
            _inLen += got;
        }
    }

    /// <summary>Descarta o que já passou, mantendo a janela de busca para trás.</summary>
    private void Compact()
    {
        int drop = (int)_pos - Search - Hop;
        if (drop <= Hop * 4) return;
        Array.Copy(_in, drop, _in, 0, _inLen - drop);
        _inLen -= drop;
        _pos -= drop;
    }
}
