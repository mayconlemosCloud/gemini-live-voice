using System.Windows;
using System.Windows.Threading;

namespace GeminiTranslateV2;

public partial class App : Application
{
    public App()
    {
        // Captura TODAS as exceções não tratadas, para que bugs (inclusive na inicialização,
        // quando a janela nem chega a aparecer) fiquem registrados no log da sessão.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("App", "EXCEÇÃO não tratada (AppDomain): " + (e.ExceptionObject as Exception)?.ToString());
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("App", "EXCEÇÃO de Task não observada: " + e.Exception);
            e.SetObserved();
        };

        Log.Write("App", $"iniciando — v{typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · .NET {Environment.Version}");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("App", "EXCEÇÃO na UI (Dispatcher): " + e.Exception);
        MessageBox.Show(
            "Ocorreu um erro:\n\n" + e.Exception.Message + "\n\nDetalhes completos no log:\n" + Log.Folder,
            "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        // Marca como tratada para o app não fechar sozinho por causa de um erro isolado.
        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Write("App", "OnStartup — abrindo janela principal.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("App", $"OnExit — código {e.ApplicationExitCode}.");
        base.OnExit(e);
    }
}
