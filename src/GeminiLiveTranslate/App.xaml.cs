using System.Windows;
using System.Windows.Threading;
using GeminiLiveTranslate.Logging;

namespace GeminiLiveTranslate;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();

        // Capture everything that would otherwise crash silently.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI", "DispatcherUnhandledException", ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error("Domain", "UnhandledException (terminating=" + ex.IsTerminating + ")", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Task", "UnobservedTaskException", ex.Exception);
            ex.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", "Encerrando aplicação.");
        base.OnExit(e);
    }
}
