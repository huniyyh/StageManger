using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StageManager.Win32;

/// <summary>Small helpers for our own windows (the strip and the hotkey sink).</summary>
public static class NativeWindow
{
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Keeps a window out of Alt-Tab and prevents clicks on it from stealing focus.</summary>
    public static void MakeNoActivateToolWindow(nint hwnd)
    {
        var h = new HWND(hwnd);
        nint exStyle = PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public static bool RegisterHotKey(nint hwnd, int id, bool ctrl, bool alt, bool shift, bool win, uint virtualKey)
    {
        var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (ctrl) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (alt) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (shift) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (win) mods |= HOT_KEY_MODIFIERS.MOD_WIN;
        return PInvoke.RegisterHotKey(new HWND(hwnd), id, mods, virtualKey);
    }

    public static void UnregisterHotKey(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);
}
