namespace GeminiTranslate.Core.Contracts;

/// <summary>
/// Registro, por direção, do que foi mandado ao modelo e do que ele devolveu.
/// </summary>
/// <remarks>
/// É a partir disso que se responde "o problema está no que mandamos ou no que veio". Falhar
/// aqui nunca pode impedir a tradução de rodar: o diagnóstico é opcional.
/// </remarks>
public interface IDiagnosticRecorder : IDisposable
{
    /// <summary>Registra um chunk enviado à rede.</summary>
    void WriteSent(byte[] pcm);

    /// <summary>Registra um chunk de tradução recebido.</summary>
    void WriteReceived(byte[] pcm);
}

/// <summary>
/// Gravação da conversa inteira em dois canais: o que você ouve e o que eles ouvem.
/// </summary>
public interface IConversationRecorder : IDisposable
{
    /// <summary>Saída renderizada da direção "Entrada". Chamado na thread de renderização.</summary>
    void WriteIncoming(float[] buffer, int offset, int count);

    /// <summary>Saída renderizada da direção "Saída". Chamado na thread de renderização.</summary>
    void WriteOutgoing(float[] buffer, int offset, int count);
}

/// <summary>Transcrição em texto das duas direções, intercaladas.</summary>
public interface ITranscriptSink : IDisposable
{
    /// <summary>Acrescenta um fragmento ao fluxo de <paramref name="label"/>.</summary>
    void Append(string label, string fragment);
}

/// <summary>Leitura e gravação das preferências do usuário.</summary>
public interface ISettingsStore
{
    /// <summary>Carrega as preferências salvas, ou os padrões quando não há nada utilizável.</summary>
    Configuration.Settings Load();

    /// <summary>Grava as preferências.</summary>
    void Save(Configuration.Settings settings);
}

/// <summary>Destino das linhas de log. Implementado pela infraestrutura.</summary>
public interface ILogSink
{
    /// <summary>Grava uma linha já formatada.</summary>
    void Write(string line);
}
