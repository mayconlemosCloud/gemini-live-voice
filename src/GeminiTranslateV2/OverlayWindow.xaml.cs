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

    public OverlayWindow(AssistantClient assistant, ConversationContext context)
    {
        InitializeComponent();
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
        await RunAsync("analisando print…", async ct =>
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

        await RunAsync("analisando região…", async ct =>
        {
            byte[] png = ScreenCapture.Capture(region.X, region.Y, region.Width, region.Height);
            return await _assistant.AnalyzeImageAsync(png, _context.GetRecent(), ct);
        });
    }

    private async void OnSuggest(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Log.Write("Overlay", "ação: sugerir da conversa.");
        if (_context.IsEmpty)
        {
            ShowResult("Ainda não há conversa suficiente para sugerir. Deixe a tradução rodar um pouco.");
            return;
        }
        await RunAsync("pensando na resposta…", ct => _assistant.SuggestFromConversationAsync(_context.GetRecent(), ct));
    }

    /// <summary>Esconde o overlay, espera o repaint, captura, e reexibe.</summary>
    private async Task<byte[]> CaptureHidingOverlay(Func<byte[]> capture)
    {
        Visibility = Visibility.Hidden;
        await Task.Delay(120);
        try { return capture(); }
        finally { Visibility = Visibility.Visible; }
    }

    private async Task RunAsync(string busyMsg, Func<CancellationToken, Task<string>> op)
    {
        _busy = true;
        SetButtonsEnabled(false);
        StatusText.Text = busyMsg;
        ShowResult("…");
        try
        {
            var result = await op(CancellationToken.None);
            ShowResult(result);
            StatusText.Text = "pronto";
        }
        catch (Exception ex)
        {
            ShowResult("Erro: " + ex.Message);
            StatusText.Text = "erro";
            Log.Write("Overlay", "falha na ação: " + ex);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool on)
    {
        ScreenButton.IsEnabled = RegionButton.IsEnabled = SuggestButton.IsEnabled = on;
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
