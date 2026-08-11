using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GeminiTranslate.App.Platform;

/// <summary>
/// Registra atalhos globais de teclado para uma janela e os libera no descarte.
/// </summary>
/// <remarks>
/// Globais quer dizer que funcionam com outro app em foco, que é o ponto: os atalhos são usados
/// durante a reunião, com o Teams ou o Zoom na frente. O P/Invoke e o hook de mensagem estavam
/// duplicados na janela principal e no overlay.
/// </remarks>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly IntPtr _handle;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = [];
    private bool _disposed;

    /// <summary>
    /// Prende-se ao handle da janela. Só pode ser criado depois que o HWND existe — em
    /// <c>SourceInitialized</c> ou <c>Loaded</c>.
    /// </summary>
    public GlobalHotkey(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(OnWindowMessage);
    }

    /// <summary>
    /// Registra Ctrl+Shift+<paramref name="key"/>. Retorna false quando o atalho já está em uso
    /// por outro aplicativo.
    /// </summary>
    /// <param name="id">Identificador único do atalho dentro desta janela.</param>
    /// <param name="key">Código virtual da tecla.</param>
    /// <param name="action">O que executar quando o atalho for acionado.</param>
    public bool RegisterControlShift(int id, uint key, Action action)
    {
        bool registered = RegisterHotKey(_handle, id, ModControl | ModShift | ModNoRepeat, key);
        if (registered) _actions[id] = action;
        return registered;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (!_actions.TryGetValue(wParam.ToInt32(), out var action)) return IntPtr.Zero;

        action();
        handled = true;
        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _actions.Keys)
            try { UnregisterHotKey(_handle, id); } catch { }
        _actions.Clear();

        _source?.RemoveHook(OnWindowMessage);
    }
}

/// <summary>Códigos virtuais das teclas usadas pelos atalhos do app.</summary>
public static class VirtualKeys
{
    /// <summary>Tecla 0.</summary>
    public const uint D0 = 0x30;

    /// <summary>Tecla 1.</summary>
    public const uint D1 = 0x31;

    /// <summary>Tecla 2.</summary>
    public const uint D2 = 0x32;

    /// <summary>Tecla 3.</summary>
    public const uint D3 = 0x33;
}
