using System.Windows;
using System.Windows.Input;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Barra fina sempre no topo, sobre o app de reunião, com as três ações do assistente.
/// </summary>
/// <remarks>
/// É um CONTROLE REMOTO, não uma segunda tela do assistente. A conversa acontece num lugar só, a
/// aba Assistente da janela principal; esta barra apenas dispara as ações e traz aquela janela à
/// frente com o resultado. Antes as duas tinham chat próprio, com históricos separados para a
/// mesma função.
///
/// As ações também respondem a atalhos GLOBAIS, que funcionam com o Teams ou o Zoom em foco:
/// Ctrl+Shift+1 print da tela, Ctrl+Shift+2 região, Ctrl+Shift+3 sugerir.
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int HotkeyScreen = 1;
    private const int HotkeyRegion = 2;
    private const int HotkeySuggest = 3;

    private readonly AssistantController _assistant;
    private GlobalHotkey? _hotkeys;

    /// <param name="assistant">Controlador compartilhado com a aba da janela principal.</param>
    public OverlayWindow(AssistantController assistant)
    {
        InitializeComponent();
        Stealth.Register(this);

        _assistant = assistant;
        _assistant.StatusChanged += OnStatusChanged;

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

        bool screen = _hotkeys.RegisterControlShift(HotkeyScreen, VirtualKeys.D1,
            () => _ = _assistant.CaptureScreenAsync());
        bool region = _hotkeys.RegisterControlShift(HotkeyRegion, VirtualKeys.D2,
            () => _ = _assistant.CaptureRegionAsync());
        bool suggest = _hotkeys.RegisterControlShift(HotkeySuggest, VirtualKeys.D3,
            () => _ = _assistant.SuggestAsync());

        Log.Write("Overlay", $"hotkeys registradas: tela={screen} regiao={region} sugerir={suggest}");
        if (!screen || !region || !suggest)
            StatusText.Text = "atalhos podem estar em uso por outro app";
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        _assistant.StatusChanged -= OnStatusChanged;
        _hotkeys?.Dispose();
        _hotkeys = null;
        Log.Write("Overlay", "barra fechada; hotkeys liberadas.");
    }

    private void OnStatusChanged(string status) =>
        Dispatcher.BeginInvoke(() => StatusText.Text = status);

    private void OnScreen(object sender, RoutedEventArgs e) => _ = _assistant.CaptureScreenAsync();

    private void OnRegion(object sender, RoutedEventArgs e) => _ = _assistant.CaptureRegionAsync();

    private void OnSuggest(object sender, RoutedEventArgs e) => _ = _assistant.SuggestAsync();

    private void OnDragBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try { DragMove(); } catch { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
