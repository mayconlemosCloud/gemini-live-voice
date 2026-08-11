using GeminiTranslate.Core.Configuration;

namespace GeminiTranslate.Core.Contracts;

/// <summary>Resultado da tentativa de assumir os dispositivos padrão do sistema.</summary>
/// <param name="Scope">Descartar restaura o estado anterior. Null quando nada foi trocado.</param>
/// <param name="Note">Texto para a interface explicando o que foi ou não aplicado.</param>
public sealed record DefaultDevicesResult(IDisposable? Scope, string Note);

/// <summary>
/// Tudo o que a sessão precisa construir e que depende do sistema operacional, da rede ou do
/// disco.
/// </summary>
/// <remarks>
/// É a porta de saída principal do núcleo. Existe porque montar uma sessão exige criar objetos
/// concretos — capturas, saídas, clientes HTTP, arquivos — e sem esta indireção a orquestração
/// voltaria a importar NAudio e WebSocket, que é exatamente o acoplamento que a arquitetura
/// pretende impedir. Uma implementação de mentira desta interface permite exercitar a sessão
/// inteira sem hardware.
/// </remarks>
public interface ITranslationPlatform
{
    /// <summary>Cria a captura da Entrada e devolve o rótulo efetivo do que está sendo escutado.</summary>
    (IAudioSource Source, string Label) CreateEntradaSource(AudioSourceChoice choice);

    /// <summary>Cria a captura do microfone real.</summary>
    IAudioSource CreateMicrophone(string deviceId);

    /// <summary>Cria a saída de áudio de uma direção.</summary>
    /// <param name="deviceId">Endpoint onde tocar.</param>
    /// <param name="originalRate">Taxa nativa da voz original misturada por baixo.</param>
    /// <param name="originalVolume">Volume inicial da voz original, de 0 a 1.</param>
    /// <param name="catchUp">Se a tradução pode acelerar para recuperar fila.</param>
    /// <param name="tag">Origem exibida no log.</param>
    IAudioSink CreateSink(string deviceId, int originalRate, float originalVolume, bool catchUp, string tag);

    /// <summary>Abre um cliente de tradução ao vivo.</summary>
    ITranslationStream CreateTranslationStream(
        string apiKey, string model, string targetLang, int inputRate, string tag);

    /// <summary>Cria o conversor para a taxa de entrada da rede.</summary>
    IResampler CreateWireResampler(int inputRate);

    /// <summary>Cria o assistente de respostas.</summary>
    IAssistant CreateAssistant(string apiKey, string model, string persona);

    /// <summary>Cria a gravação de diagnóstico de uma direção.</summary>
    IDiagnosticRecorder CreateDiagnostics(string name);

    /// <summary>Cria a gravação estéreo da conversa.</summary>
    /// <param name="stamp">Carimbo de tempo que identifica os arquivos da sessão.</param>
    /// <param name="incoming">Formato do mix da direção "Entrada".</param>
    /// <param name="outgoing">Formato do mix da direção "Saída".</param>
    IConversationRecorder CreateConversationRecorder(string stamp, AudioFormat incoming, AudioFormat outgoing);

    /// <summary>Cria a transcrição em texto da sessão.</summary>
    ITranscriptSink CreateTranscript(string stamp);

    /// <summary>Assume os dispositivos padrão do sistema durante a sessão.</summary>
    /// <param name="settings">Preferências, para achar o microfone virtual.</param>
    /// <param name="entradaDeviceId">Cabo escutado, ou null quando a Entrada é um processo.</param>
    DefaultDevicesResult ApplyDefaultDevices(Settings settings, string? entradaDeviceId);
}
