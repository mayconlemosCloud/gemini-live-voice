using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GeminiTranslate.App.Ui;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Infrastructure.Persistence;

namespace GeminiTranslate.App;

/// <summary>
/// Ponto de entrada do aplicativo.
/// </summary>
/// <remarks>
/// Captura TODAS as exceções não tratadas para que defeitos fiquem registrados no log da sessão,
/// inclusive os de inicialização, quando a janela nem chega a aparecer.
/// </remarks>
public partial class App : Application
{
    /// <summary>Instala os capturadores de exceção e registra o ambiente.</summary>
    public App()
    {
        AppComposition.StartLogging();

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log.Write("App", $"iniciando — v{typeof(App).Assembly.GetName().Version} · " +
                         $"{Environment.OSVersion} · .NET {Environment.Version}");
    }

    /// <summary>
    /// Mostra o erro e marca como tratado, para o app não fechar sozinho por causa de uma falha
    /// isolada na interface.
    /// </summary>
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("App", "EXCEÇÃO na UI (Dispatcher): " + e.Exception);

        MessageBox.Show(
            "Ocorreu um erro:\n\n" + e.Exception.Message + "\n\nDetalhes completos no log:\n" + AppPaths.Logs,
            "Erro", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }

    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Log.Write("App", "EXCEÇÃO não tratada (AppDomain): " + (e.ExceptionObject as Exception));

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Write("App", "EXCEÇÃO de Task não observada: " + e.Exception);
        e.SetObserved();
    }

    /// <summary>Monta os adaptadores e abre a janela principal.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = AppComposition.Create();
        Log.Write("App", "OnStartup — abrindo janela principal.");
        new MainWindow(services).Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("App", $"OnExit — código {e.ApplicationExitCode}.");
        base.OnExit(e);
    }
}
