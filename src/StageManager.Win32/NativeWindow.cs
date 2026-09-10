using StageManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StageManager.Win32;

/// <summary>Small helpers for our own windows (the strip and the hotkey sink).</summary>
public static class NativeWindow
{
    private const nint WS_EX_TRANSPARENT = 0x00000020;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private static readonly HWND HWND_TOPMOST = new(-1);

    /// <summary>Keeps a window out of Alt-Tab and prevents clicks on it from stealing focus.</summary>
    public static void MakeNoActivateToolWindow(nint hwnd)
    {
        var h = new HWND(hwnd);
        nint exStyle = PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Lets all mouse input pass through a layered window to whatever is underneath.</summary>
    public static void MakeClickThrough(nint hwnd)
    {
        var h = new HWND(hwnd);
        nint exStyle = PInvoke.GetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
    }

    /// <summary>Puts a window at the top of the topmost band without activating it.</summary>
    public static void RaiseTopmost(nint hwnd)
        => PInvoke.SetWindowPos(new HWND(hwnd), HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>The pointer position in physical pixels.</summary>
    public static PointPx GetCursorPosition()
    {
        PInvoke.GetCursorPos(out System.Drawing.Point p);
        return new PointPx(p.X, p.Y);
    }

    /// <summary>
    /// Whether the primary mouse button is down right now, straight from the input system. Needed because a
    /// window that never activates cannot capture the mouse and so stops hearing about it once the pointer leaves.
    /// </summary>
    public static bool IsLeftButtonDown()
    {
        const int VK_LBUTTON = 0x01;
        return (PInvoke.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
    }

    /// <summary>Whether Shift is held, straight from the input system (a never-activated window has no keyboard focus).</summary>
    public static bool IsShiftDown()
    {
        const int VK_SHIFT = 0x10;
        return (PInvoke.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
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
