using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.App.Platform;

/// <summary>
/// Esconde as janelas do app de gravações e compartilhamento de tela (Teams, Meet, Zoom, OBS,
/// PrintScreen).
/// </summary>
/// <remarks>
/// Usa SetWindowDisplayAffinity com WDA_EXCLUDEFROMCAPTURE: o usuário continua vendo a janela
/// normalmente, mas ela some do que é capturado. Em Windows anteriores ao 10 2004 cai para
/// WDA_MONITOR, em que a janela aparece preta na captura — o que ainda oculta o conteúdo.
/// </remarks>
public static class Stealth
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

    private const uint AffinityNone = 0x00;
    private const uint AffinityMonitor = 0x01;
    private const uint AffinityExcludeFromCapture = 0x11;

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    /// <summary>Estado atual. Janelas registradas depois herdam este valor.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Liga ou desliga a ocultação em todas as janelas WPF abertas.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (Application.Current is { } app)
            foreach (Window window in app.Windows)
                ApplyToHandle(new WindowInteropHelper(window).Handle);

        Log.Write("Stealth", on
            ? "oculto em compartilhamento de tela."
            : "visível em compartilhamento de tela.");
    }

    /// <summary>
    /// Aplica o estado atual à janela: agora, se ela já tem handle, e a cada recriação do handle.
    /// </summary>
    public static void Register(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyToHandle(new WindowInteropHelper(window).Handle);
        ApplyToHandle(new WindowInteropHelper(window).Handle);
    }

    /// <summary>
    /// Tira, ou devolve, a janela do Alt+Tab e do Task View, marcando-a como tool window.
    /// </summary>
    /// <remarks>
    /// Necessário além do display affinity: a miniatura desenhada pelo Alt+Tab é uma janela do
    /// sistema, que continua sendo capturada mesmo com a nossa janela excluída. A troca de estilo
    /// só vale depois de esconder e reexibir a janela.
    /// </remarks>
    public static void SetHiddenFromAltTab(Window window, bool hidden)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int current = GetWindowLong(hwnd, GwlExStyle);
        int wanted = hidden
            ? (current | WsExToolWindow) & ~WsExAppWindow
            : current & ~WsExToolWindow;
        if (current == wanted) return;

        bool visible = window.IsVisible;
        if (visible) window.Hide();
        SetWindowLong(hwnd, GwlExStyle, wanted);
        if (visible)
        {
            window.Show();
            window.Activate();
        }
    }

    /// <summary>Aplica o estado atual a um HWND qualquer, inclusive de janelas WinForms.</summary>
    public static void ApplyToHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (!Enabled)
        {
            SetWindowDisplayAffinity(hwnd, AffinityNone);
            return;
        }

        if (SetWindowDisplayAffinity(hwnd, AffinityExcludeFromCapture)) return;

        int error = Marshal.GetLastWin32Error();
        if (!SetWindowDisplayAffinity(hwnd, AffinityMonitor))
            Log.Write("Stealth", $"não foi possível ocultar a janela da captura (erro {error}).");
    }
}
