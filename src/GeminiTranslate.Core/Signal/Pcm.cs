namespace GeminiTranslate.Core.Signal;

/// <summary>
/// Operações sobre PCM16 little-endian mono, o formato em que o áudio circula entre a captura,
/// a rede e os medidores.
/// </summary>
/// <remarks>
/// Existe para que RMS e conversão de amostra tenham UMA definição. Estiveram copiados em cinco
/// classes, e qualquer divergência entre elas desalinharia silenciosamente os limiares de fala
/// que <c>LatencyProbe</c>, <c>SpeechBalance</c> e <c>InputGain</c> comparam entre si.
/// </remarks>
public static class Pcm
{
    /// <summary>Bytes por amostra de 16 bits.</summary>
    public const int BytesPerSample = 2;

    /// <summary>Amostra de índice <paramref name="index"/>, normalizada para -1..1.</summary>
    public static float SampleAt(byte[] pcm, int index)
    {
        short raw = (short)(pcm[index * BytesPerSample] | (pcm[index * BytesPerSample + 1] << 8));
        return raw / 32768f;
    }

    /// <summary>Escreve <paramref name="value"/> (-1..1) na amostra de índice <paramref name="index"/>.</summary>
    public static void WriteSample(byte[] pcm, int index, float value)
    {
        short raw = (short)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        pcm[index * BytesPerSample] = (byte)(raw & 0xFF);
        pcm[index * BytesPerSample + 1] = (byte)((raw >> 8) & 0xFF);
    }

    /// <summary>Nível RMS (0..1) do buffer inteiro.</summary>
    public static float Rms(byte[] pcm) => Rms(pcm, 0, pcm.Length);

    /// <summary>Nível RMS (0..1) de <paramref name="count"/> bytes a partir de <paramref name="offset"/>.</summary>
    public static float Rms(byte[] pcm, int offset, int count)
    {
        int samples = count / BytesPerSample;
        if (samples == 0) return 0f;

        double sum = 0;
        for (int i = 0; i < samples; i++)
        {
            short raw = (short)(pcm[offset + i * BytesPerSample] | (pcm[offset + i * BytesPerSample + 1] << 8));
            float f = raw / 32768f;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / samples);
    }

    /// <summary>Maior valor absoluto (0..1) do buffer — o pico que não pode clipar.</summary>
    public static float Peak(byte[] pcm)
    {
        int samples = pcm.Length / BytesPerSample;
        float peak = 0f;
        for (int i = 0; i < samples; i++)
        {
            float f = Math.Abs(SampleAt(pcm, i));
            if (f > peak) peak = f;
        }
        return peak;
    }

    /// <summary>Duração, em milissegundos, de um buffer PCM16 mono na taxa informada.</summary>
    public static double DurationMs(byte[] pcm, int sampleRate) =>
        pcm.Length / (double)BytesPerSample * 1000 / sampleRate;
}
