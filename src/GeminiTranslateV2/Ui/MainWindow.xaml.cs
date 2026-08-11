using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Session;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Janela principal: escolher dispositivos e idiomas, iniciar e parar a tradução, e acompanhar o
/// que está sendo dito.
/// </summary>
/// <remarks>
/// A mecânica da chamada mora na <see cref="TranslationSession"/>; aqui só existe interface. O
/// mapeamento entre controles e preferências está no arquivo parcial MainWindow.Settings.cs.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Atalho global que mostra ou esconde a janela.</summary>
    /// <remarks>
    /// Necessário porque, em modo oculto, a janela não está na barra de tarefas nem no Alt+Tab.
    /// </remarks>
    public const string ShowHotkeyText = "Ctrl+Shift+0";

    private const int HotkeyToggleWindow = 10;
    private static readonly TimeSpan DelayRefresh = TimeSpan.FromMilliseconds(500);

    private readonly AppServices _services;
    private readonly Settings _settings;
    private readonly ConversationContext _context = new();
    private readonly SuggestionPresenter _suggestions;

    private TranslationSession? _session;
    private QuestionTranscript? _questionTranscript;
    private OverlayWindow? _overlay;
    private BalanceWindow? _balance;
    private GlobalHotkey? _hotkey;

    private bool Running => _session is not null;

    /// <param name="services">Adaptadores concretos vindos da raiz de composição.</param>
    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _settings = services.Settings.Load();
        _suggestions = new SuggestionPresenter(this);

        ApplyStealth(_settings.HideFromScreenShare);
        Stealth.Register(this);

        LoadDevices();
        LoadSources();
        ApplySettings();

        StartDelayTimer();

        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) =>
        {
            SaveSettings();
            StopSession();
            _hotkey?.Dispose();
        };
    }

    private void StartDelayTimer()
    {
        var timer = new DispatcherTimer { Interval = DelayRefresh };
        timer.Tick += (_, _) => UpdateDelayText();
        timer.Start();
    }

    /// <summary>
    /// Registra o atalho global. Só pode acontecer aqui: o HWND precisa existir, e trocar o
    /// estilo de janela para escondê-la do Alt+Tab depende disso — mas a janela ainda não apareceu.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Stealth.SetHiddenFromAltTab(this, Stealth.Enabled);

        _hotkey = new GlobalHotkey(this);
        bool registered = _hotkey.RegisterControlShift(HotkeyToggleWindow, VirtualKeys.D0, ToggleWindowVisibility);

        Log.Write("Stealth", $"atalho {ShowHotkeyText} (mostrar/esconder janela) registrado: {registered}");
        if (!registered) StatusText.Text = $"atalho {ShowHotkeyText} em uso por outro app";
    }

    /// <summary>Esconde a janela se ela está à vista e em foco; caso contrário mostra e traz à frente.</summary>
    private void ToggleWindowVisibility()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    /// <summary>Atualiza os dois indicadores de atraso, ou os esconde quando parado.</summary>
    private void UpdateDelayText()
    {
        if (_session is null)
        {
            DelayPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DelayPanel.Visibility = Visibility.Visible;

        (DelayOutText.Text, DelayOutText.Foreground) = LagFormat.Describe("você", _session.Outgoing);
        (DelayInText.Text, DelayInText.Foreground) = LagFormat.Describe("eles", _session.Incoming);
    }

    private void OnRefreshSources(object sender, RoutedEventArgs e) => LoadSources();

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (Running)
        {
            StopSession();
            SetUiRunning(false);
            return;
        }

        SaveSettings();
        await StartSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        StatusText.Text = "Conectando…";
        StartButton.IsEnabled = false;

        try
        {
            ResetTranscriptPanels();

            var choice = (SourceCombo.SelectedItem as SourceOption)?.ToChoice();
            _session = await TranslationSession.StartAsync(_settings, choice, _context, _services.Platform);

            WireSession(_session);
            ShowSessionWindows(_session);
            ShowSessionHeaders(_session);
            SetUiRunning(true);
        }
        catch (Exception ex)
        {
            StopSession();
            SetUiRunning(false);
            StatusText.Text = "Erro: " + ex.Message;
            Log.Write("UI", "falha ao iniciar: " + ex);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void ResetTranscriptPanels()
    {
        IncomingBox.Document.Blocks.Clear();
        OutgoingBox.Clear();
    }

    private void WireSession(TranslationSession session)
    {
        _questionTranscript = new QuestionTranscript(
            IncomingBox,
            session.Assistant is null ? null : (question, context) => OpenSuggestion(session, question, context),
            _context.NoteQuestion);

        WireIncoming(session.Incoming);
        WireOutgoing(session.Outgoing);
    }

    /// <summary>A Entrada passa pelo <see cref="QuestionTranscript"/>, que detecta perguntas.</summary>
    private void WireIncoming(TranslationDirection direction)
    {
        direction.TranslatedText += text =>
            Dispatcher.BeginInvoke(() => _questionTranscript?.Append(text));
        direction.Status += status => Dispatcher.BeginInvoke(() => StatusText.Text = status);
    }

    /// <summary>O painel mostra o que ELES ouvem, ou seja, a tradução já no idioma deles.</summary>
    private void WireOutgoing(TranslationDirection direction)
    {
        direction.TranslatedText += text => Dispatcher.BeginInvoke(() =>
        {
            OutgoingBox.AppendText(text);
            OutgoingBox.ScrollToEnd();
        });
        direction.Status += status => Dispatcher.BeginInvoke(() => StatusText.Text = status);
    }

    /// <summary>
    /// Abre as janelas auxiliares da sessão.
    /// </summary>
    /// <remarks>
    /// O overlay só faz sentido com o assistente ativo. A etiqueta de saldo aparece sempre: a
    /// janela principal fica minimizada durante a chamada, e é justamente aí que se quer saber o
    /// que falta sair.
    /// </remarks>
    private void ShowSessionWindows(TranslationSession session)
    {
        if (session.Assistant is not null)
        {
            _overlay = new OverlayWindow(session.Assistant, session.Context, _services.ScreenCapture);
            _overlay.Closed += (_, _) => _overlay = null;
            _overlay.Show();
        }

        _balance = new BalanceWindow(_settings, _services.Settings);
        _balance.Closed += (_, _) => _balance = null;
        _balance.Show();
        _balance.Bind(session.Outgoing, session.Incoming);
    }

    private void ShowSessionHeaders(TranslationSession session)
    {
        IncomingHeader.Text = $"{session.IncomingLabel} → você ouve em {Languages.ByCode(_settings.MyLang).Name}";
        OutgoingHeader.Text = $"Você → eles ouvem em {Languages.ByCode(_settings.TheirLang).Name}";
        DefaultDevicesText.Text = session.DefaultDevicesNote;
    }

    /// <summary>
    /// Abre a janela de sugestão e busca a resposta na IA — só ao clicar, para poupar cota.
    /// </summary>
    /// <remarks>
    /// O contexto vindo do painel só tem o lado "Eles"; a conversa acumulada tem os dois lados e
    /// explica melhor uma pergunta ambígua, então ela tem preferência.
    /// </remarks>
    private async void OpenSuggestion(TranslationSession session, string question, string fallbackContext)
    {
        if (session.Assistant is null)
        {
            Log.Write("Sugestão", "clique ignorado: assistente desligado.");
            return;
        }

        var context = _context.IsEmpty ? fallbackContext : _context.GetRecent();
        await _suggestions.ShowAsync(session.Assistant, question, context);
    }

    private void StopSession()
    {
        try { _suggestions.Close(); } catch { }
        try { _overlay?.Close(); } catch { }
        try { _balance?.Close(); } catch { }
        try { _session?.Dispose(); } catch { }

        _overlay = null;
        _balance = null;
        _session = null;
        _questionTranscript = null;
    }

    private void SetUiRunning(bool running)
    {
        StartButton.Content = running ? "■  Parar" : "▶  Iniciar";
        StatusText.Text = running ? "Traduzindo ao vivo…" : "Parado";
        MuteButton.IsEnabled = running;
        MuteButton.Content = "🎙 Mic ligado";

        foreach (var control in ConfigurationControls()) control.IsEnabled = !running;
        if (!running) DefaultDevicesText.Text = "";
    }

    /// <summary>Controles que não podem mudar com a tradução no ar.</summary>
    private Control[] ConfigurationControls() =>
    [
        SourceCombo, RefreshSourcesButton, HeadphonesCombo, MicCombo, VirtualMicCombo,
        MyLangCombo, TheirLangCombo, ApiKeyBox, AssistantEnabledCheck, AssistantContextBox,
        MakeDefaultCheck, CatchUpCheck
    ];

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        _session.Outgoing.Muted = !_session.Outgoing.Muted;
        MuteButton.Content = _session.Outgoing.Muted ? "🔇 Mic mudo" : "🎙 Mic ligado";
    }

    private void OnStealthToggle(object sender, RoutedEventArgs e) => ApplyStealth(!Stealth.Enabled);

    /// <summary>Liga ou desliga a ocultação em todas as janelas do app e reflete no botão.</summary>
    private void ApplyStealth(bool on)
    {
        Stealth.SetEnabled(on);
        Stealth.SetHiddenFromAltTab(this, on);
        _settings.HideFromScreenShare = on;

        StealthButton.Content = on ? "🕶 Oculto na tela" : "👁 Visível na tela";
        StealthButton.ToolTip = on
            ? "Ligado: o app não aparece em compartilhamento de tela, gravação, print nem no " +
              $"Alt+Tab.\n{ShowHotkeyText} mostra/esconde esta janela. Clique para deixá-la visível."
            : "Desligado: o app aparece normalmente para quem vê sua tela e volta ao Alt+Tab. " +
              "Clique para ocultar.";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;

        UpdateVolumeText();

        float volume = (float)VolumeSlider.Value;
        if (_session is not null)
        {
            _session.Incoming.OriginalVolume = volume;
            _session.Outgoing.OriginalVolume = volume;
        }
        _settings.OriginalVolume = volume;
    }

    private void UpdateVolumeText() => VolumeText.Text = $"{VolumeSlider.Value:P0}";
}
