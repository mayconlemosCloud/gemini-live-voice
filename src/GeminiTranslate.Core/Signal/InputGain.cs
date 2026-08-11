using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Ganho automático do áudio que vai para a rede, no lugar do AGC que o navegador aplicava de
/// graça no AI Studio (getUserMedia liga autoGainControl por padrão; WASAPI não liga nada).
/// A captura daqui saía com pico em 0,35 da escala e mediana de fala em cerca de −33 dBFS.
/// </summary>
/// <remarks>
/// REGRA NÚMERO UM: NUNCA CLIPAR. Distorção é infinitamente pior que sinal baixo. A primeira
/// versão tinha alvo 0,70 FS com passo simétrico de 0,5 dB e produziu 0,338% de amostras
/// clipadas, concentradas no ATAQUE de cada frase. O resultado foi o modelo transcrever
/// "teste som" como "É, ainda são.": clipping gera distorção harmônica, que arruína o
/// rastreamento de pitch, que é de onde sai a entonação. Ganho agressivo causa exatamente o
/// problema que tentava resolver.
///
/// Três coisas que esta versão faz e a primeira não fazia:
///
/// 1. Ataque instantâneo, release lento. Se o ganho atual ameaça estourar o chunk, ele cai na
///    hora para o valor seguro; subir é que é lento. A primeira versão descia 0,5 dB por chunk,
///    então cada início de frase clipava por cerca de 1,5 s antes de o ganho recuar.
/// 2. Envelope congelado no silêncio. A primeira versão atualizava o envelope
///    incondicionalmente, então numa pausa ele decaía, o ganho subia sozinho e o ataque da frase
///    seguinte entrava alto demais. Sem fala, agora nada se move.
/// 3. Alvo conservador com teto baixo, deixando margem para o transiente de uma sílaba forte,
///    sempre bem mais alto que o envelope da frase.
///
/// Não é um compressor: o ganho é único por chunk, então dentro de uma frase todas as amostras
/// são multiplicadas pelo mesmo número — a forma da fala fica intacta, só sobe de patamar.
/// Comprimir sílaba a sílaba apagaria a variação de intensidade que se quer preservar. Também
/// não é um gate: o ganho vale para todo chunk, silêncio incluído.
/// </remarks>
public sealed class InputGain
{
    /// <summary>Alvo conservador de envelope, com headroom de sobra para transiente.</summary>
    private const float TargetPeak = 0.45f;

    /// <summary>Acima deste pico o ataque instantâneo entra.</summary>
    private const float SafePeak = 0.90f;

    /// <summary>Nunca atenua: sinal já forte passa intacto.</summary>
    private const float MinGain = 1.0f;

    /// <summary>Teto de +9,5 dB, para não virar amplificador de ruído.</summary>
    private const float MaxGain = 3.0f;

    /// <summary>Pico de chunk abaixo disto é fundo de sala, não fala.</summary>
    private const float SpeechPeak = 0.05f;

    /// <summary>Subida por chunk, lenta o bastante para ser inaudível.</summary>
    private const double ReleaseStepDb = 0.3;

    private const long LogIntervalMs = 15000;

    private readonly string _tag;
    private float _gain = 1.0f;
    private float _envelope;
    private long _lastLogAt;
    private int _clipped;

    /// <param name="tag">Origem exibida no log.</param>
    public InputGain(string tag) => _tag = tag;

    /// <summary>Aplica o ganho no lugar, sobre um chunk mono PCM16.</summary>
    public void Apply(byte[] pcm)
    {
        int samples = pcm.Length / Pcm.BytesPerSample;
        if (samples == 0) return;

        float peak = Pcm.Peak(pcm);
        Attack(peak);
        Release(peak);

        if (_gain <= 1.0001f) return;

        for (int i = 0; i < samples; i++)
        {
            float amplified = Pcm.SampleAt(pcm, i) * _gain;
            if (amplified > 1f || amplified < -1f) _clipped++;
            Pcm.WriteSample(pcm, i, amplified);
        }
    }

    /// <summary>Recua o ganho sem rampa quando este chunk estouraria com o valor atual.</summary>
    private void Attack(float peak)
    {
        if (peak > 1e-6f && peak * _gain > SafePeak)
            _gain = Math.Max(MinGain, SafePeak / peak);
    }

    /// <summary>
    /// Sobe o ganho em passos pequenos, e só com fala presente. No silêncio, envelope e ganho
    /// ficam exatamente onde estão.
    /// </summary>
    private void Release(float peak)
    {
        if (peak < SpeechPeak) return;

        _envelope = Math.Max(peak, _envelope * 0.9f + peak * 0.1f);
        float wanted = Math.Clamp(TargetPeak / _envelope, MinGain, MaxGain);
        if (wanted > _gain)
            _gain = Math.Min(wanted, _gain * (float)Math.Pow(10, ReleaseStepDb / 20.0));

        LogOccasionally();
    }

    /// <summary>
    /// Relata o ganho em uso. A contagem de amostras clipadas DEVE ficar em zero: qualquer valor
    /// aqui é o ataque instantâneo falhando.
    /// </summary>
    private void LogOccasionally()
    {
        long now = Environment.TickCount64;
        if (now - _lastLogAt < LogIntervalMs) return;
        _lastLogAt = now;

        Log.Write(_tag, $"ganho de entrada: {20 * Math.Log10(_gain):0.0} dB " +
                        $"(envelope da fala em {_envelope:0.000} FS, alvo {TargetPeak:0.00}, " +
                        $"amostras clipadas: {_clipped}).");
    }
}
