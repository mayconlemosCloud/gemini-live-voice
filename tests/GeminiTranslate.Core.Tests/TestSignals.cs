using GeminiTranslate.Core.Signal;

namespace GeminiTranslate.Core.Tests;

/// <summary>Geradores de áudio sintético usados pelos testes.</summary>
internal static class TestSignals
{
    /// <summary>Chunk mono PCM16 de silêncio digital.</summary>
    public static byte[] Silence(int samples) => new byte[samples * Pcm.BytesPerSample];

    /// <summary>Senoide de amplitude e frequência dadas.</summary>
    public static byte[] Sine(int samples, float amplitude, double frequency, int sampleRate)
    {
        var pcm = new byte[samples * Pcm.BytesPerSample];
        for (int i = 0; i < samples; i++)
            Pcm.WriteSample(pcm, i, (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate)));
        return pcm;
    }

    /// <summary>Chunk de amplitude constante, útil para exercitar picos sem depender de fase.</summary>
    public static byte[] Constant(int samples, float amplitude)
    {
        var pcm = new byte[samples * Pcm.BytesPerSample];
        for (int i = 0; i < samples; i++)
            Pcm.WriteSample(pcm, i, i % 2 == 0 ? amplitude : -amplitude);
        return pcm;
    }

    /// <summary>Um chunk de 100 ms na taxa informada.</summary>
    public static int ChunkSamples(int sampleRate) => sampleRate / 10;
}
