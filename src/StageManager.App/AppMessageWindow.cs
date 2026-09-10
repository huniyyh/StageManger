using System.Windows.Interop;
using StageManager.Win32;

namespace StageManager.App;

/// <summary>
/// One invisible top-level window that receives everything the app listens for outside WPF: the global toggle
/// hotkey (Ctrl+Alt+S), notification-area icon interactions, "another instance was started", and Explorer's
/// TaskbarCreated broadcast. It is a real window rather than a message-only one because the tray menu needs an owner.
/// </summary>
internal sealed class AppMessageWindow : IDisposable
{
    /// <summary>Title of the window; a second instance looks it up to talk to the first.</summary>
    public const string WindowTitle = "StageManager.Messages";

    /// <summary>Sent by a second instance before it exits. wParam is 1 when it was started with --enable.</summary>
    public const uint WM_ANOTHER_INSTANCE = 0x8000 + 1; // WM_APP + 1

    private const int HotkeyId = 0x5347;
    private const int WM_HOTKEY = 0x0312;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;

    public nint Handle => _source.Handle;

    public event Action? HotkeyPressed;

    /// <summary>Another instance was launched; the argument says whether it asked to enable Stage Manager.</summary>
    public event Action<bool>? AnotherInstanceStarted;

    public event Action<TrayEvent>? TrayInteraction;
    public event Action? TaskbarCreated;

    public bool HotkeyRegistered { get; }

    public AppMessageWindow()
    {
        var parameters = new HwndSourceParameters(WindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP, // never shown: no WS_VISIBLE
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _taskbarCreated = NotificationIcon.RegisterTaskbarCreatedMessage();
        HotkeyRegistered = NativeWindow.RegisterHotKey(_source.Handle, HotkeyId, ctrl: true, alt: true, shift: false, win: false, virtualKey: 'S');
        if (!HotkeyRegistered) Log.Write("hotkey registration failed; is Ctrl+Alt+S taken by another app?");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        else if (msg == WM_ANOTHER_INSTANCE)
        {
            AnotherInstanceStarted?.Invoke(wParam != IntPtr.Zero);
            handled = true;
        }
        else if (msg == NotificationIcon.CallbackMessage)
        {
            var interaction = NotificationIcon.Interpret(lParam);
            if (interaction != TrayEvent.None) TrayInteraction?.Invoke(interaction);
            handled = true;
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            TaskbarCreated?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        NativeWindow.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }
}
