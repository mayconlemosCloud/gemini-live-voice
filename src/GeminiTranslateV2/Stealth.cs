using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GeminiTranslateV2;

/// <summary>
/// Esconde as janelas do app de gravações e compartilhamento de tela (Teams, Meet, Zoom, OBS, PrintScreen).
/// Usa SetWindowDisplayAffinity com WDA_EXCLUDEFROMCAPTURE: você continua vendo a janela normalmente,
/// mas ela some do que é capturado. Em Windows anteriores ao 10 2004 cai para WDA_MONITOR
/// (a janela aparece preta na captura, que ainda oculta o conteúdo).
/// </summary>
public static class Stealth
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const uint WDA_NONE = 0x00;
    private const uint WDA_MONITOR = 0x01;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    /// <summary>Estado atual — novas janelas registradas herdam este valor.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Liga/desliga em todas as janelas WPF abertas.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (Application.Current is { } app)
            foreach (Window w in app.Windows)
                ApplyToHandle(new WindowInteropHelper(w).Handle);
        Log.Write("Stealth", on ? "oculto em compartilhamento de tela." : "visível em compartilhamento de tela.");
    }

    /// <summary>Aplica o estado atual à janela — agora, se ela já tem handle, e a cada recriação do handle.</summary>
    public static void Register(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyToHandle(new WindowInteropHelper(window).Handle);
        ApplyToHandle(new WindowInteropHelper(window).Handle);
    }

    /// <summary>
    /// Tira (ou devolve) a janela do Alt+Tab e do Task View, marcando-a como tool window.
    /// Necessário além do display affinity: a miniatura desenhada pelo Alt+Tab é uma janela do
    /// sistema, que continua sendo capturada mesmo com a nossa janela excluída.
    /// Trocar o estilo só vale depois de esconder/reexibir a janela.
    /// </summary>
    public static void SetHiddenFromAltTab(Window window, bool hidden)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        int wanted = hidden
            ? (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW
            : (ex & ~WS_EX_TOOLWINDOW);
        if (ex == wanted) return;

        bool visible = window.IsVisible;
        if (visible) window.Hide();
        SetWindowLong(hwnd, GWL_EXSTYLE, wanted);
        if (visible) { window.Show(); window.Activate(); }
    }

    /// <summary>Aplica o estado atual a um HWND qualquer (inclusive janelas WinForms).</summary>
    public static void ApplyToHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!Enabled) { SetWindowDisplayAffinity(hwnd, WDA_NONE); return; }

        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
        {
            // Windows < 10 2004 não conhece WDA_EXCLUDEFROMCAPTURE.
            int err = Marshal.GetLastWin32Error();
            if (!SetWindowDisplayAffinity(hwnd, WDA_MONITOR))
                Log.Write("Stealth", $"não foi possível ocultar a janela da captura (erro {err}).");
        }
    }
}
