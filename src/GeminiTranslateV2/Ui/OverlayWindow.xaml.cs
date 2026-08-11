using System.Text;
using System.Windows;
using System.Windows.Input;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Session;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Barra flutuante sempre no topo, sobre o app de reunião, com as ações de IA.
/// </summary>
/// <remarks>
/// As ações funcionam por botão ou por atalho GLOBAL, ou seja, mesmo com o Teams ou o Zoom em
/// foco: Ctrl+Shift+1 tira print da tela, Ctrl+Shift+2 seleciona uma região e Ctrl+Shift+3 pede
/// uma sugestão. Usa o contexto acumulado da conversa e o assistente da sessão.
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int HotkeyScreen = 1;
    private const int HotkeyRegion = 2;
    private const int HotkeySuggest = 3;

    /// <summary>Tempo para o overlay sumir da tela antes de uma captura.</summary>
    private const int HideBeforeCaptureMs = 120;

    private readonly IAssistant _assistant;
    private readonly ConversationContext _context;
    private readonly IScreenCapture _screen;
    private GlobalHotkey? _hotkeys;
    private bool _busy;

    /// <summary>
    /// Chat do assistente: serve ao mesmo tempo de tela e de histórico mandado à API.
    /// </summary>
    /// <remarks>
    /// As ações de botão também entram aqui, com um turno de usuário sintético descrevendo o que
    /// foi pedido, para que o usuário possa continuar a conversa em cima do resultado ("explica
    /// melhor", "e se eu responder X?") em vez de cada ação ser um fato isolado. É separado do
    /// <see cref="ConversationContext"/>, que é a transcrição da reunião: limpar um não mexe no
    /// outro.
    /// </remarks>
    private readonly List<ChatTurn> _chat = [];

    /// <param name="assistant">Assistente já configurado.</param>
    /// <param name="context">Transcrição acumulada da reunião.</param>
    /// <param name="screen">Captura de tela para as ações de print e região.</param>
    public OverlayWindow(IAssistant assistant, ConversationContext context, IScreenCapture screen)
    {
        InitializeComponent();
        Stealth.Register(this);

        _assistant = assistant;
        _context = context;
        _screen = screen;

        Loaded += OnLoaded;
        Closed += OnClosedCleanup;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PositionAtTopCenter();
        RegisterHotkeys();
    }

    private void PositionAtTopCenter()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 8;
    }

    private void RegisterHotkeys()
    {
        _hotkeys = new GlobalHotkey(this);

        bool screen = _hotkeys.RegisterControlShift(HotkeyScreen, VirtualKeys.D1, () => _ = CaptureScreenAsync());
        bool region = _hotkeys.RegisterControlShift(HotkeyRegion, VirtualKeys.D2, () => _ = CaptureRegionAsync());
        bool suggest = _hotkeys.RegisterControlShift(HotkeySuggest, VirtualKeys.D3, () => _ = SuggestAsync());

        Log.Write("Overlay", $"hotkeys registradas: tela={screen} regiao={region} sugerir={suggest}");
        if (!screen || !region || !suggest)
            StatusText.Text = "atalhos podem estar em uso por outro app";
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        Log.Write("Overlay", "fechado; hotkeys liberadas.");
    }

    private void OnScreen(object sender, RoutedEventArgs e) => _ = CaptureScreenAsync();

    private void OnRegion(object sender, RoutedEventArgs e) => _ = CaptureRegionAsync();

    private void OnSuggest(object sender, RoutedEventArgs e) => _ = SuggestAsync();

    private async Task CaptureScreenAsync()
    {
        if (_busy) return;

        Log.Write("Overlay", "ação: print da tela.");
        await RunAsync("analisando print…", "(print da tela toda)", async ct =>
        {
            byte[] png = await CaptureHidingOverlayAsync(_screen.CaptureFull);
            return await _assistant.AnalyzeImageAsync(png, _context.GetRecent(), ct);
        });
    }

    private async Task CaptureRegionAsync()
    {
        if (_busy) return;

        Log.Write("Overlay", "ação: selecionar região.");
        var region = await SelectRegionAsync();
        if (region.Width <= 0)
        {
            StatusText.Text = "seleção cancelada";
            return;
        }

        await RunAsync("analisando região…", "(print de uma região da tela)", async ct =>
        {
            byte[] png = _screen.Capture(region.X, region.Y, region.Width, region.Height);
            return await _assistant.AnalyzeImageAsync(png, _context.GetRecent(), ct);
        });
    }

    /// <summary>Esconde o overlay, deixa o usuário arrastar a região, e reexibe.</summary>
    private async Task<System.Drawing.Rectangle> SelectRegionAsync()
    {
        Visibility = Visibility.Hidden;
        await Task.Delay(HideBeforeCaptureMs);

        try
        {
            using var form = new RegionSelectForm();
            return form.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? form.SelectedRegion
                : default;
        }
        finally
        {
            Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Sugere uma resposta.
    /// </summary>
    /// <remarks>
    /// Se a outra pessoa acabou de perguntar algo, responder ESSA pergunta é sempre mais útil que
    /// sugerir falas genéricas — mas ela sozinha costuma ser ambígua, então vai com a conversa
    /// inteira como contexto.
    /// </remarks>
    private async Task SuggestAsync()
    {
        if (_busy) return;

        if (_context.IsEmpty)
        {
            Log.Write("Overlay", "ação: sugerir — sem conversa acumulada.");
            ShowResult("Ainda não há conversa suficiente para sugerir. Deixe a tradução rodar um pouco.");
            return;
        }

        var question = _context.RecentQuestion;
        if (question is not null)
        {
            Log.Write("Overlay", $"ação: responder a última pergunta — '{question}'");
            await RunAsync("respondendo a última pergunta…", $"(o que respondo a: \"{question}\"?)",
                ct => _assistant.SuggestAnswerAsync(question, _context.GetRecent(), ct));
            return;
        }

        Log.Write("Overlay", "ação: sugerir da conversa (sem pergunta recente).");
        await RunAsync("pensando na resposta…", "(o que eu posso responder agora?)",
            ct => _assistant.SuggestFromConversationAsync(_context.GetRecent(), ct));
    }

    private void OnSend(object sender, RoutedEventArgs e) => _ = SendCurrentInputAsync();

    /// <summary>Enter envia; Shift+Enter continua na linha de baixo.</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        _ = SendCurrentInputAsync();
    }

    private async Task SendCurrentInputAsync()
    {
        if (_busy) return;

        var question = InputBox.Text.Trim();
        if (question.Length == 0) return;

        InputBox.Text = "";
        Log.Write("Overlay", $"chat: pergunta do usuário ({question.Length} chars).");

        await RunAsync("pensando…", question,
            ct => _assistant.ChatAsync(_chat, _context.GetRecent(), ct));
    }

    private void OnClearChat(object sender, RoutedEventArgs e)
    {
        _chat.Clear();
        ResultBox.Text = "";
        StatusText.Text = "chat limpo";
        Log.Write("Overlay", "chat do assistente limpo (a transcrição da reunião continua).");
    }

    /// <summary>Esconde o overlay, espera o repintar, captura, e reexibe.</summary>
    private async Task<byte[]> CaptureHidingOverlayAsync(Func<byte[]> capture)
    {
        Visibility = Visibility.Hidden;
        await Task.Delay(HideBeforeCaptureMs);

        try { return capture(); }
        finally { Visibility = Visibility.Visible; }
    }

    /// <summary>
    /// Executa uma chamada ao assistente e registra o par pergunta/resposta no chat.
    /// </summary>
    /// <param name="busyMessage">O que mostrar na barra de estado durante a chamada.</param>
    /// <param name="userTurn">
    /// O que aparece como fala do usuário: a pergunta digitada, ou uma descrição da ação para os
    /// botões. Entra no chat ANTES da chamada, porque <see cref="IAssistant.ChatAsync"/>
    /// espera o histórico terminando na pergunta atual.
    /// </param>
    /// <param name="operation">A chamada em si.</param>
    private async Task RunAsync(string busyMessage, string userTurn, Func<CancellationToken, Task<string>> operation)
    {
        _busy = true;
        SetControlsEnabled(false);
        StatusText.Text = busyMessage;

        _chat.Add(new ChatTurn(true, userTurn));
        RenderChat(pending: true);

        try
        {
            var answer = await operation(CancellationToken.None);
            _chat.Add(new ChatTurn(false, answer));
            StatusText.Text = "pronto";
        }
        catch (Exception ex)
        {
            ShowFailure(userTurn, ex);
            return;
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }

        RenderChat();
    }

    /// <summary>
    /// Mostra o erro no chat mas o mantém FORA do histórico mandado à API: repetir a pergunta
    /// depois não pode arrastar junto o texto de "limite atingido".
    /// </summary>
    private void ShowFailure(string userTurn, Exception error)
    {
        _chat.RemoveAt(_chat.Count - 1);
        RenderChat();
        AppendErrorLine(userTurn, error.Message);
        StatusText.Text = "erro";
        Log.Write("Overlay", "falha na ação: " + error);
    }

    private void SetControlsEnabled(bool enabled)
    {
        ScreenButton.IsEnabled = RegionButton.IsEnabled = SuggestButton.IsEnabled = enabled;
        SendButton.IsEnabled = InputBox.IsEnabled = enabled;
    }

    /// <summary>Redesenha o chat inteiro e rola para o fim, onde está o que acabou de chegar.</summary>
    private void RenderChat(bool pending = false)
    {
        var text = new StringBuilder();
        foreach (var turn in _chat)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(turn.FromUser ? "🧑 Você: " : "🤖 ").Append(turn.Text);
        }
        if (pending) text.Append("\n\n🤖 …");

        ShowResult(text.ToString(), scrollToEnd: true);
    }

    private void AppendErrorLine(string userTurn, string message)
    {
        var text = new StringBuilder(ResultBox.Text);
        if (text.Length > 0) text.Append("\n\n");
        text.Append("🧑 Você: ").Append(userTurn).Append("\n\n⚠️ ").Append(message);

        ShowResult(text.ToString(), scrollToEnd: true);
    }

    private void ShowResult(string text, bool scrollToEnd = false)
    {
        ResultBox.Text = text;
        if (scrollToEnd) ResultBox.ScrollToEnd();
        ResultPanel.Visibility = Visibility.Visible;
        Activate();
    }

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try { DragMove(); } catch { }
    }

    private void OnTogglePanel(object sender, RoutedEventArgs e) =>
        ResultPanel.Visibility = ResultPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
