using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Infrastructure;
using GeminiTranslate.Infrastructure.Persistence;
using GeminiTranslate.Infrastructure.Windows;

namespace GeminiTranslate.App;

/// <summary>Os adaptadores concretos que a interface entrega ao núcleo.</summary>
/// <param name="Settings">Leitura e gravação das preferências.</param>
/// <param name="Platform">Fábrica de tudo o que depende do sistema.</param>
/// <param name="ScreenCapture">Captura de tela para as ações de IA.</param>
public sealed record AppServices(
    ISettingsStore Settings,
    ITranslationPlatform Platform,
    IScreenCapture ScreenCapture);

/// <summary>
/// Raiz de composição: o único lugar do aplicativo que instancia infraestrutura.
/// </summary>
/// <remarks>
/// Concentrar as construções aqui é o que permite que nenhuma janela e nenhuma classe de sessão
/// conheça uma implementação concreta. Não há contêiner de injeção de propósito: com meia dúzia
/// de dependências, montá-las à mão é mais claro e falha em tempo de compilação.
/// </remarks>
public static class AppComposition
{
    /// <summary>Liga o log ao disco. Feito antes de tudo, para nenhuma linha se perder.</summary>
    public static void StartLogging() => Log.UseSink(new FileLogSink());

    /// <summary>Monta os adaptadores concretos.</summary>
    public static AppServices Create() =>
        new(
            new SettingsStore(),
            new WindowsTranslationPlatform(),
            new WindowsScreenCapture());
}
