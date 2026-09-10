using System.Windows.Interop;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>Owns a message-only window that receives the global toggle hotkey (Ctrl+Alt+S).</summary>
internal sealed class HotkeyHost : IDisposable
{
    private const int HotkeyId = 0x5347;
    private const int WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HwndSource _source;

    public event Action? Pressed;
    public bool Registered { get; }

    public HotkeyHost()
    {
        var parameters = new HwndSourceParameters("StageManager.Hotkey")
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
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeWindow.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }
}
