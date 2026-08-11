using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace GeminiTranslateV2;

/// <summary>
/// Barra flutuante sempre-no-topo, sobre o app de reunião. Aciona as ações de IA por botão ou
/// por atalho GLOBAL (funcionam mesmo com o Teams/Zoom em foco):
///   Ctrl+Shift+1 = print da tela · Ctrl+Shift+2 = selecionar região · Ctrl+Shift+3 = sugerir.
/// Usa o contexto acumulado da conversa (ConversationContext) e o AssistantClient (Gemini).
/// </summary>
public partial class OverlayWindow : Window
{
    // ---- Win32 hotkeys globais ----
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;
    private const int HK_SCREEN = 1, HK_REGION = 2, HK_SUGGEST = 3;

    private readonly AssistantClient _assistant;
    private readonly ConversationContext _context;
    private HwndSource? _source;
    private bool _busy;

    /// <summary>
    /// Chat do assistente: serve ao mesmo tempo de tela e de histórico mandado para a API. As ações
    /// de botão (print, região, sugerir) também entram aqui, com um turno de usuário sintético
    /// descrevendo o que foi pedido — assim o usuário pode continuar a conversa em cima do resultado
    /// ("explica melhor", "e se eu responder X?") em vez de cada ação ser um fato isolado.
    /// É separado do <see cref="ConversationContext"/>, que é a transcrição da reunião; limpar um
    /// não mexe no outro.
    /// </summary>
    private readonly List<AssistantClient.ChatTurn> _chat = [];

    public OverlayWindow(AssistantClient assistant, ConversationContext context)
    {
        InitializeComponent();
        Stealth.Register(this);
        _assistant = assistant;
        _context = context;
        Loaded += OnLoaded;
        Closed += OnClosedCleanup;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Posiciona no topo, centralizado no monitor primário.
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 8;

        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        bool ok1 = RegisterHotKey(handle, HK_SCREEN, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x31); // '1'
        bool ok2 = RegisterHotKey(handle, HK_REGION, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x32); // '2'
        bool ok3 = RegisterHotKey(handle, HK_SUGGEST, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x33); // '3'
        Log.Write("Overlay", $"hotkeys registradas: tela={ok1} regiao={ok2} sugerir={ok3}");
        if (!ok1 || !ok2 || !ok3)
            StatusText.Text = "atalhos podem estar em uso por outro app";
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        try { UnregisterHotKey(handle, HK_SCREEN); UnregisterHotKey(handle, HK_REGION); UnregisterHotKey(handle, HK_SUGGEST); } catch { }
        _source?.RemoveHook(WndProc);
        Log.Write("Overlay", "fechado; hotkeys liberadas.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HK_SCREEN: OnScreen(this, new RoutedEventArgs()); handled = true; break;
                case HK_REGION: OnRegion(this, new RoutedEventArgs()); handled = true; break;
                case HK_SUGGEST: OnSuggest(this, new RoutedEventArgs()); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    // ---- Ações ----

    private async void OnScreen(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Log.Write("Overlay", "ação: print da tela.");
        await RunAsync("analisando print…", "(print da tela toda)", async ct =>
        {
            byte[] png = await CaptureHidingOverlay(() => ScreenCapture.CaptureFull());
            return await _assistant.AnalyzeImageAsync(png, _context.GetRecent(), ct);
        });
    }

    private async void OnRegion(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Log.Write("Overlay", "ação: selecionar região.");
        // Esconde o overlay durante a seleção/captura.
        Visibility = Visibility.Hidden;
        await Task.Delay(120);
        System.Drawing.Rectangle region = default;
        try
        {
            using var form = new RegionSelectForm();
            if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                region = form.SelectedRegion;
        }
        finally { Visibility = Visibility.Visible; }

        if (region.Width <= 0) { StatusText.Text = "seleção cancelada"; return; }

        await RunAsync("analisando região…", "(print de uma região da tela)", async ct =>
        {
            byte[] png = ScreenCapture.Capture(region.X, region.Y, region.Width, region.Height);
            return await _assistant.AnalyzeImageAsync(png, _context.GetRecent(), ct);
        });
    }

    private async void OnSuggest(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_context.IsEmpty)
        {
            Log.Write("Overlay", "ação: sugerir — sem conversa acumulada.");
            ShowResult("Ainda não há conversa suficiente para sugerir. Deixe a tradução rodar um pouco.");
            return;
        }

        // Se a outra pessoa acabou de perguntar algo, responder ESSA pergunta é sempre mais útil
        // que sugerir falas genéricas — mas ela sozinha costuma ser ambígua, então vai com a
        // conversa inteira como contexto.
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

    /// <summary>Pergunta livre digitada no campo de texto.</summary>
    private async void OnSend(object sender, RoutedEventArgs e) => await SendCurrentInputAsync();

    /// <summary>Enter envia; Shift+Enter continua na linha de baixo.</summary>
    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        await SendCurrentInputAsync();
    }

    private async Task SendCurrentInputAsync()
    {
        if (_busy) return;
        var question = InputBox.Text.Trim();
        if (question.Length == 0) return;

        // Limpa o campo já: se a chamada demorar, o usuário não fica olhando a própria pergunta
        // ainda no campo sem saber se foi ou não.
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

    /// <summary>Esconde o overlay, espera o repaint, captura, e reexibe.</summary>
    private async Task<byte[]> CaptureHidingOverlay(Func<byte[]> capture)
    {
        Visibility = Visibility.Hidden;
        await Task.Delay(120);
        try { return capture(); }
        finally { Visibility = Visibility.Visible; }
    }

    /// <summary>
    /// Executa uma chamada ao assistente e registra o par pergunta/resposta no chat.
    /// <paramref name="userTurn"/> é o que aparece como fala do usuário — a pergunta digitada, ou
    /// uma descrição da ação para os botões. Ele entra no chat ANTES da chamada, porque
    /// <see cref="AssistantClient.ChatAsync"/> espera o histórico terminando na pergunta atual.
    /// </summary>
    private async Task RunAsync(string busyMsg, string userTurn, Func<CancellationToken, Task<string>> op)
    {
        _busy = true;
        SetButtonsEnabled(false);
        StatusText.Text = busyMsg;

        _chat.Add(new AssistantClient.ChatTurn(true, userTurn));
        RenderChat(pending: true);

        try
        {
            var result = await op(CancellationToken.None);
            _chat.Add(new AssistantClient.ChatTurn(false, result));
            StatusText.Text = "pronto";
        }
        catch (Exception ex)
        {
            // O erro fica no chat como resposta, mas FORA do histórico mandado à API: repetir a
            // pergunta depois não pode arrastar junto o texto de "Erro: limite atingido".
            _chat.RemoveAt(_chat.Count - 1);
            RenderChat();
            AppendErrorLine(userTurn, ex.Message);
            StatusText.Text = "erro";
            Log.Write("Overlay", "falha na ação: " + ex);
            return;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }

        RenderChat();
    }

    private void SetButtonsEnabled(bool on)
    {
        ScreenButton.IsEnabled = RegionButton.IsEnabled = SuggestButton.IsEnabled = on;
        SendButton.IsEnabled = InputBox.IsEnabled = on;
    }

    /// <summary>Redesenha o chat inteiro e rola para o fim, onde está o que acabou de chegar.</summary>
    private void RenderChat(bool pending = false)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var turn in _chat)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(turn.FromUser ? "🧑 Você: " : "🤖 ").Append(turn.Text);
        }
        if (pending) sb.Append("\n\n🤖 …");

        ResultBox.Text = sb.ToString();
        ResultBox.ScrollToEnd();
        ResultPanel.Visibility = Visibility.Visible;
        Activate();
    }

    private void AppendErrorLine(string userTurn, string message)
    {
        var sb = new System.Text.StringBuilder(ResultBox.Text);
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append("🧑 Você: ").Append(userTurn).Append("\n\n⚠️ ").Append(message);
        ResultBox.Text = sb.ToString();
        ResultBox.ScrollToEnd();
        ResultPanel.Visibility = Visibility.Visible;
        Activate();
    }

    private void ShowResult(string text)
    {
        ResultBox.Text = text;
        ResultPanel.Visibility = Visibility.Visible;
        Activate();
    }

    // ---- UI ----

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch { }
    }

    private void OnTogglePanel(object sender, RoutedEventArgs e) =>
        ResultPanel.Visibility = ResultPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ResultBox.Text); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
