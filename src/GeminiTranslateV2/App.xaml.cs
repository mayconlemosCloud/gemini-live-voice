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
    /// <summary>
    /// Instala os capturadores de exceção, liga o log e registra o ambiente — nessa ordem.
    /// </summary>
    /// <remarks>
    /// Os capturadores vêm PRIMEIRO de propósito. Ligar o log toca o disco e a infraestrutura, e
    /// uma falha ali antes de eles existirem derruba o processo sem mensagem e sem registro — foi
    /// exatamente o que aconteceu quando o carregamento do assembly de infraestrutura falhou.
    /// Agora o app segue em pé e o erro aparece na tela, mesmo que o log não exista.
    /// </remarks>
    public App()
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        StartLoggingSafely();

        Log.Write("App", $"iniciando — v{typeof(App).Assembly.GetName().Version} · " +
                         $"{Environment.OSVersion} · .NET {Environment.Version}");
    }

    /// <summary>
    /// Liga o log em arquivo. Sem log o app funciona; morrer por causa dele seria desproporcional.
    /// </summary>
    private static void StartLoggingSafely()
    {
        try
        {
            AppComposition.StartLogging();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "O registro em arquivo não pôde ser iniciado; o app continua funcionando sem ele.\n\n"
                + ex.Message,
                "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    /// <summary>
    /// Monta os adaptadores e abre a janela principal.
    /// </summary>
    /// <remarks>
    /// Se a montagem falhar não há janela nenhuma para abrir, e um processo vivo e invisível é
    /// pior que um encerramento: o usuário não vê nada, não tem o que fechar, e o app ainda
    /// aparece no gerenciador de tarefas. Então explica o que houve e encerra.
    /// </remarks>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppServices services;
        try
        {
            services = AppComposition.Create();
        }
        catch (Exception ex)
        {
            Log.Write("App", "falha ao montar o aplicativo: " + ex);
            MessageBox.Show(
                "O aplicativo não pôde ser iniciado:\n\n" + ex.Message,
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

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
