using GeminiTranslate.Core.Contracts;

namespace GeminiTranslate.App.Ui;

/// <summary>Um endpoint de áudio, como aparece nos combos de dispositivo.</summary>
/// <param name="Id">Identificador do endpoint.</param>
/// <param name="Name">Nome amigável exibido.</param>
public sealed record DeviceItem(string Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Um processo com janela visível, como aparece no combo de origem.</summary>
/// <param name="ProcessName">Nome do executável, sem extensão.</param>
/// <param name="Id">PID no momento em que a lista foi montada.</param>
/// <param name="Title">Título da janela principal.</param>
public sealed record ProcessItem(string ProcessName, int Id, string Title)
{
    /// <inheritdoc />
    public override string ToString() => $"{Title} ({ProcessName})";
}

/// <summary>
/// Uma opção do combo de origem da Entrada, exibível e conversível na escolha que a sessão
/// entende.
/// </summary>
public abstract record SourceOption
{
    /// <summary>Converte para a escolha usada pela camada de sessão.</summary>
    public abstract AudioSourceChoice ToChoice();
}

/// <summary>Escutar o áudio de um aplicativo.</summary>
/// <param name="Process">O processo escolhido.</param>
public sealed record ProcessSourceOption(ProcessItem Process) : SourceOption
{
    /// <inheritdoc />
    public override AudioSourceChoice ToChoice() => new ProcessSourceChoice(Process.ProcessName);

    /// <inheritdoc />
    public override string ToString() => $"Processo: {Process}";
}

/// <summary>Escutar um dispositivo ou cabo virtual.</summary>
/// <param name="Device">O endpoint escolhido.</param>
public sealed record DeviceSourceOption(DeviceItem Device) : SourceOption
{
    /// <inheritdoc />
    public override AudioSourceChoice ToChoice() => new DeviceSourceChoice(Device.Id, Device.Name);

    /// <inheritdoc />
    public override string ToString() => $"Dispositivo: {Device.Name}";
}
