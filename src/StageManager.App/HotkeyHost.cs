using System.Windows.Interop;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>
/// Owns a message-only window that receives the global toggle hotkey (Ctrl+Alt+S) and messages from
/// other instances of the app that were started while this one is running.
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    /// <summary>Title of the message-only window; a second instance looks it up to talk to the first.</summary>
    public const string WindowTitle = "StageManager.Hotkey";

    /// <summary>Sent by a second instance before it exits. wParam is 1 when it was started with --enable.</summary>
    public const uint WM_ANOTHER_INSTANCE = 0x8000 + 1; // WM_APP + 1

    private const int HotkeyId = 0x5347;
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;

    public event Action? Pressed;

    /// <summary>Another instance was launched; the argument says whether it asked to enable Stage Manager.</summary>
    public event Action<bool>? AnotherInstanceStarted;

    public bool Registered { get; }

    public HotkeyHost()
    {
        var parameters = new HwndSourceParameters(WindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        Registered = NativeWindow.RegisterHotKey(_source.Handle, HotkeyId, ctrl: true, alt: true, shift: false, win: false, virtualKey: 'S');
        if (!Registered) Log.Write("hotkey registration failed; is Ctrl+Alt+S taken by another app?");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        else if (msg == WM_ANOTHER_INSTANCE)
        {
            AnotherInstanceStarted?.Invoke(wParam != IntPtr.Zero);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeWindow.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }
}
