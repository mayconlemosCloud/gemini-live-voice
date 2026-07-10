using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using GeminiLiveTranslate.Logging;

namespace GeminiLiveTranslate.Input;

/// <summary>
/// System-wide low-level keyboard hook that raises <see cref="Pressed"/> when a
/// single target key goes down — even when this app is NOT the focused window.
/// That lets push-to-talk work while you stay in Meet/Teams instead of having to
/// bring this window to the front and hold a key.
/// </summary>
public sealed class GlobalKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly int _vk;
    private readonly LowLevelKeyboardProc _proc;   // keep the delegate rooted so the GC can't collect it
    private IntPtr _hookId = IntPtr.Zero;

    /// <summary>Raised on the UI thread each time the target key is pressed.</summary>
    public event Action? Pressed;

    public GlobalKeyHook(Key key)
    {
        _vk = KeyInterop.VirtualKeyFromKey(key);
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            Log.Warn("Hotkey", $"Falha ao instalar o hook global (erro {Marshal.GetLastWin32Error()}).");
        else
            Log.Info("Hotkey", "Hotkey global ativo.");
    }

    // Runs on the thread that installed the hook (the WPF UI thread, which pumps
    // messages), so raising Pressed here is already on the UI thread.
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
            if (vk == _vk) Pressed?.Invoke();
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
