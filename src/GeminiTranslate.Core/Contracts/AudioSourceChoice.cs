namespace GeminiTranslate.Core.Contracts;

/// <summary>O que a direção "Entrada" deve escutar.</summary>
/// <remarks>
/// É só a ESCOLHA, sem nenhuma noção de como capturar: quem resolve isso numa captura concreta
/// é o adaptador de plataforma. É o que permite validar e testar a escolha sem placa de som.
/// </remarks>
public abstract record AudioSourceChoice
{
    /// <summary>Rótulo curto para o cabeçalho da interface.</summary>
    public abstract string Label { get; }
}

/// <summary>Escutar o áudio de um aplicativo específico.</summary>
/// <param name="ProcessName">Nome do processo, como "Teams" ou "chrome".</param>
public sealed record ProcessSourceChoice(string ProcessName) : AudioSourceChoice
{
    /// <inheritdoc />
    public override string Label => ProcessName;
}

/// <summary>Escutar um endpoint de reprodução, tipicamente um cabo virtual.</summary>
/// <param name="DeviceId">Identificador do endpoint.</param>
/// <param name="DeviceName">Nome amigável do endpoint.</param>
public sealed record DeviceSourceChoice(string DeviceId, string DeviceName) : AudioSourceChoice
{
    /// <inheritdoc />
    public override string Label => DeviceName;
}
