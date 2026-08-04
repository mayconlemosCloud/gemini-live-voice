namespace GeminiTranslateV2;

/// <summary>
/// Ganho automático do áudio que vai para a rede, no lugar do AGC que o navegador aplicava de
/// graça no AI Studio (getUserMedia liga autoGainControl por padrão; WASAPI não liga nada).
/// A captura daqui saía com pico em 0,35 da escala e mediana de fala em ~−33 dBFS.
///
/// REGRA NÚMERO UM: NUNCA CLIPAR. Distorção é infinitamente pior que sinal baixo. A primeira
/// versão disto tinha alvo 0,70 FS com passo simétrico de 0,5 dB e produziu 0,338% de amostras
/// clipadas concentradas no ATAQUE de cada frase (medido em Saída-enviado-20260729-192510.wav).
/// O resultado foi o modelo transcrever "teste som" como "É, ainda são." — clipping gera
/// distorção harmônica, que arruína o rastreamento de pitch, que é de onde sai a entonação.
/// Ou seja: ganho agressivo causa exatamente o problema que ele tentava resolver.
///
/// Três coisas que essa versão faz e a primeira não fazia:
///
/// 1. ATAQUE INSTANTÂNEO, RELEASE LENTO (assimétrico). Se o ganho atual ameaça estourar este
///    chunk, ele cai NA HORA para o valor seguro. Subir é que é lento. A primeira versão descia
///    a 0,5 dB por chunk, então cada início de frase clipava por ~1,5 s antes de o ganho recuar.
///
/// 2. ENVELOPE CONGELADO NO SILÊNCIO. A primeira versão atualizava _envPeak incondicionalmente e
///    só congelava a adaptação do ganho, então numa pausa o envelope decaía, o ganho subia sozinho
///    e o ataque da frase seguinte entrava com ganho alto demais. Agora, sem fala, nada se move.
///
/// 3. ALVO CONSERVADOR (<see cref="TargetPeak"/>) com teto baixo. Deixa margem de sobra para o
///    transiente de uma sílaba forte, que é sempre bem mais alto que o envelope da frase.
///
/// NÃO É UM COMPRESSOR: o ganho é único por chunk e move-se devagar para cima, então dentro de uma
/// frase todas as amostras são multiplicadas pelo mesmo número — a forma da fala fica intacta, só
/// sobe de patamar. Comprimir sílaba a sílaba apagaria a variação de intensidade que queremos
/// preservar. E não é um gate: o ganho vale para todo chunk, silêncio incluído.
/// </summary>
public sealed class InputGain
{
    private const float TargetPeak = 0.45f;   // conservador: headroom de sobra para transiente
    private const float SafePeak = 0.90f;     // acima disto o ataque instantâneo entra
    private const float MinGain = 1.0f;       // nunca atenua: sinal já forte passa intacto
    private const float MaxGain = 3.0f;       // +9,5 dB, teto para não virar amplificador de ruído
    private const float SpeechPeak = 0.05f;   // pico de chunk abaixo disto é fundo de sala
    private const double ReleaseStepDb = 0.3; // subida por chunk: lenta o bastante para ser inaudível

    private readonly string _tag;
    private float _gain = 1.0f;
    private float _envPeak;
    private long _lastLog;
    private int _clipped;

    public InputGain(string tag) => _tag = tag;

    /// <summary>Aplica o ganho no lugar, sobre PCM16 little-endian mono.</summary>
    public void Apply(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return;

        float peak = 0f;
        for (int i = 0; i < samples; i++)
        {
            float f = Math.Abs((short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8))) / 32768f;
            if (f > peak) peak = f;
        }

        // ATAQUE: este chunk estouraria com o ganho atual? Recua agora, sem rampa.
        if (peak > 1e-6f && peak * _gain > SafePeak)
            _gain = Math.Max(MinGain, SafePeak / peak);

        // RELEASE: só com fala presente, e só para cima, em passos pequenos. No silêncio o
        // envelope e o ganho ficam exatamente onde estão — nada de ganho subindo na pausa.
        if (peak >= SpeechPeak)
        {
            _envPeak = Math.Max(peak, _envPeak * 0.9f + peak * 0.1f);
            float want = Math.Clamp(TargetPeak / _envPeak, MinGain, MaxGain);
            if (want > _gain)
                _gain = Math.Min(want, _gain * (float)Math.Pow(10, ReleaseStepDb / 20.0));
            LogOccasionally();
        }

        if (_gain <= 1.0001f) return; // nada a fazer

        for (int i = 0; i < samples; i++)
        {
            float f = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f * _gain;
            if (f > 1f || f < -1f) { f = Math.Clamp(f, -1f, 1f); _clipped++; }
            short s = (short)Math.Round(f * 32767f);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
    }

    private void LogOccasionally()
    {
        long now = Environment.TickCount64;
        if (now - _lastLog < 15000) return;
        _lastLog = now;
        // _clipped DEVE ficar em 0. Qualquer valor aqui é o ataque instantâneo falhando.
        Log.Write(_tag, $"ganho de entrada: {20 * Math.Log10(_gain):0.0} dB " +
                        $"(envelope da fala em {_envPeak:0.000} FS, alvo {TargetPeak:0.00}, " +
                        $"amostras clipadas: {_clipped}).");
    }
}
