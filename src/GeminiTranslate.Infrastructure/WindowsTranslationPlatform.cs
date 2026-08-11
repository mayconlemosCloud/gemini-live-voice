using System.Diagnostics;
using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Infrastructure.Gemini;
using GeminiTranslate.Infrastructure.Persistence;
using GeminiTranslate.Infrastructure.Wasapi;
using NAudio.CoreAudioApi;

namespace GeminiTranslate.Infrastructure;

/// <summary>
/// Implementação Windows da porta de plataforma: WASAPI para áudio, Gemini para tradução e
/// assistente, e o sistema de arquivos para gravações.
/// </summary>
/// <remarks>
/// É o único ponto em que o núcleo encosta no sistema. Trocar de biblioteca de áudio, de provedor
/// de tradução ou de destino de gravação é reescrever esta classe, sem tocar em nada da sessão.
/// </remarks>
public sealed class WindowsTranslationPlatform : ITranslationPlatform
{
    /// <inheritdoc />
    public (IAudioSource Source, string Label) CreateEntradaSource(AudioSourceChoice choice)
    {
        switch (choice)
        {
            case DeviceSourceChoice device:
            {
                using var enumerator = new MMDeviceEnumerator();
                return (new LoopbackCapture(enumerator.GetDevice(device.DeviceId)), device.DeviceName);
            }
            case ProcessSourceChoice process:
            {
                var running = FindRunningProcess(process.ProcessName);
                return (new ProcessCapture((uint)running.Id), running.ProcessName);
            }
            default:
                throw new InvalidOperationException("origem de entrada desconhecida.");
        }
    }

    /// <summary>
    /// Resolve o PID pelo nome no momento de conectar, e não no momento em que a lista foi
    /// montada: o processo pode ter sido reiniciado desde então.
    /// </summary>
    private static Process FindRunningProcess(string processName) =>
        Process.GetProcesses().FirstOrDefault(p =>
            p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)
            && p.MainWindowHandle != IntPtr.Zero)
        ?? throw new InvalidOperationException($"'{processName}' não está mais rodando — atualize a lista.");

    /// <inheritdoc />
    public IAudioSource CreateMicrophone(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return new MicCapture(enumerator.GetDevice(deviceId));
    }

    /// <inheritdoc />
    public IAudioSink CreateSink(string deviceId, int originalRate, float originalVolume, bool catchUp, string tag)
    {
        using var enumerator = new MMDeviceEnumerator();
        return new AudioOutput(enumerator.GetDevice(deviceId), originalRate, originalVolume, catchUp, tag);
    }

    /// <inheritdoc />
    public ITranslationStream CreateTranslationStream(
        string apiKey, string model, string targetLang, int inputRate, string tag) =>
        new LiveTranslateClient(apiKey, model, targetLang, inputRate, tag);

    /// <inheritdoc />
    public IResampler CreateWireResampler(int inputRate) => new WireResampler(inputRate);

    /// <inheritdoc />
    public IAssistant CreateAssistant(string apiKey, string model, string persona) =>
        new AssistantClient(apiKey, model, persona);

    /// <inheritdoc />
    public IDiagnosticRecorder CreateDiagnostics(string name) => new DiagnosticRecorder(name);

    /// <inheritdoc />
    public IConversationRecorder CreateConversationRecorder(
        string stamp, AudioFormat incoming, AudioFormat outgoing) =>
        new ConversationRecorder(stamp, incoming, outgoing);

    /// <inheritdoc />
    public ITranscriptSink CreateTranscript(string stamp) => new TranscriptLog(stamp);

    /// <inheritdoc />
    public DefaultDevicesResult ApplyDefaultDevices(Settings settings, string? entradaDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return WindowsDefaultDevices.Apply(enumerator, settings, entradaDeviceId);
    }
}
