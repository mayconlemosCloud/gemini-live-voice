namespace GeminiTranslateV2;

/// <summary>
/// Corta o SILÊNCIO MORTO do áudio enviado — as pausas longas de quem fala pausado.
///
/// Por que isso reduz atraso: o dub volta do servidor em 1× tempo real (medido: 1,011× no log
/// session-20260729-123206), ancorado na linha do tempo do áudio que ELE recebeu. Toda pausa sua
/// que entra no stream é reproduzida como silêncio no dub e empurra tudo que vem depois para
/// frente — e como o dub nunca vem mais rápido que 1×, esse empurrão NUNCA é recuperado. Foi isso
/// que travou o offset em 22,6 s na sessão de 12:04. Não enviando o silêncio morto, a linha do
/// tempo do modelo comprime e a tradução volta a colar na sua fala.
///
/// ISTO NÃO É UM GATE DE VAD, e a distinção importa: o comentário de MicCapture/ProcessCapture
/// avisa que adivinhar silêncio localmente quebrou versões anteriores (o modelo não fechava o
/// turno e "não parava de falar"). Por isso o silêncio dos primeiros <see cref="KeepMs"/> ms de
/// cada pausa É ENVIADO INTEIRO — bem mais que os 900 ms de silenceDurationMs que o servidor
/// precisa para fechar o turno. Só o que vem DEPOIS de o turno já ter fechado é descartado, e
/// esse trecho é, por definição, dead air que não carrega informação nenhuma.
/// </summary>
public sealed class PauseTrimmer
{
    private const float SpeechRms = 0.01f;  // piso do mic medido: 0,0023 · fala p99: 0,24
    private const double KeepMs = 1500;     // > silenceDurationMs (900): o turno sempre fecha normal

    private readonly string _tag;
    private readonly double _chunkMs;
    private double _silenceMs;
    private double _skippedMs;
    private double _loggedSkipMs;

    public PauseTrimmer(string tag, int sampleRate, int chunkBytes)
    {
        _tag = tag;
        _chunkMs = chunkBytes / 2.0 / sampleRate * 1000.0;
    }

    /// <summary>True se este chunk deve ir para a rede; false se for dead air descartável.</summary>
    public bool ShouldSend(byte[] pcm)
    {
        if (Rms(pcm) >= SpeechRms)
        {
            _silenceMs = 0;
            return true;
        }

        _silenceMs += _chunkMs;
        if (_silenceMs <= KeepMs) return true; // silêncio que o servidor precisa ouvir

        _skippedMs += _chunkMs;
        if (_skippedMs - _loggedSkipMs >= 10_000)
        {
            _loggedSkipMs = _skippedMs;
            Log.Write(_tag, $"pausas cortadas: {_skippedMs / 1000:0} s de silêncio morto não enviados " +
                            $"(cada segundo aqui é um segundo a menos de atraso acumulado).");
        }
        return false;
    }

    private static float Rms(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float f = s / 32768f;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / samples);
    }
}
