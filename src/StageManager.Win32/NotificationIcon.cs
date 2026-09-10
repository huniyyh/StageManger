using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StageManager.Win32;

public enum TrayEvent
{
    None,
    LeftClick,
    LeftDoubleClick,
    ContextMenu,
    BalloonClicked,
}

/// <summary>One line of the tray menu. Id 0 is a separator.</summary>
public sealed record TrayMenuEntry(int Id, string Text, bool Checked = false, bool Enabled = true)
{
    public static readonly TrayMenuEntry Separator = new(0, "");
    public bool IsSeparator => Id == 0;
}

/// <summary>
/// A notification-area icon driven straight through Shell_NotifyIcon: icon, tooltip, balloons and a popup menu.
/// Replaces WinForms' NotifyIcon so the app does not have to load WinForms and System.Drawing at all.
/// The owner window receives <see cref="CallbackMessage"/>; feed its lParam to <see cref="Interpret"/>.
/// </summary>
public sealed unsafe class NotificationIcon : IDisposable
{
    /// <summary>WM_APP + 2, posted to the owner window for every interaction with the icon.</summary>
    public const uint CallbackMessage = 0x8000 + 2;

    private const uint IconId = 1;
    private const uint WM_NULL = 0;

    private readonly HWND _owner;
    private readonly HICON _icon;
    private string _tip;

    public NotificationIcon(nint ownerHwnd, nint icon, string tip)
    {
        _owner = new HWND(ownerHwnd);
        _icon = new HICON(icon);
        _tip = tip;
        Add();
    }

    private NOTIFYICONDATAW Data()
    {
        var data = new NOTIFYICONDATAW { hWnd = _owner, uID = IconId };
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        return data;
    }

    /// <summary>Adds the icon. Also called again when Explorer restarts and posts TaskbarCreated.</summary>
    public void Add()
    {
        var data = Data();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        Copy(_tip, data.szTip.AsSpan());
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);

        // Version 4: the pointer position arrives in wParam and the event in the low word of lParam.
        data.Anonymous.uVersion = 4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, data);
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        var data = Data();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        Copy(tip, data.szTip.AsSpan());
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
    }

    public void ShowBalloon(string title, string text, int timeoutMs)
    {
        var data = Data();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        Copy(title, data.szInfoTitle.AsSpan());
        Copy(text, data.szInfo.AsSpan());
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;
        data.Anonymous.uTimeout = (uint)timeoutMs;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
    }

    /// <summary>Shows a popup menu at the pointer and returns the chosen entry's id, or 0 when dismissed.</summary>
    public int ShowMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        using var menu = PInvoke.CreatePopupMenu_SafeHandle(); // disposing destroys the menu
        if (menu.IsInvalid) return 0;

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
                continue;
            }
            var flags = MENU_ITEM_FLAGS.MF_STRING;
            if (entry.Checked) flags |= MENU_ITEM_FLAGS.MF_CHECKED;
            if (!entry.Enabled) flags |= MENU_ITEM_FLAGS.MF_GRAYED;
            PInvoke.AppendMenu(menu, flags, (nuint)entry.Id, entry.Text);
        }

        PInvoke.GetCursorPos(out System.Drawing.Point pt);
        PInvoke.SetForegroundWindow(_owner); // without this the menu stays open when the user clicks elsewhere
        var flagsAll = TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY;
        var chosen = PInvoke.TrackPopupMenuEx(menu, (uint)flagsAll, pt.X, pt.Y, _owner, null);
        PInvoke.PostMessage(_owner, WM_NULL, default, default); // the documented follow-up so the menu closes cleanly
        return chosen.Value;
    }

    /// <summary>Turns the lParam of a <see cref="CallbackMessage"/> into what the user did.</summary>
    public static TrayEvent Interpret(nint lParam)
    {
        return ((uint)lParam & 0xFFFF) switch
        {
            0x0202 => TrayEvent.LeftClick,       // WM_LBUTTONUP
            0x0203 => TrayEvent.LeftDoubleClick, // WM_LBUTTONDBLCLK
            0x007B => TrayEvent.ContextMenu,     // WM_CONTEXTMENU
            0x0405 => TrayEvent.BalloonClicked,  // NIN_BALLOONUSERCLICK
            _ => TrayEvent.None,
        };
    }

    public static uint DoubleClickTime => PInvoke.GetDoubleClickTime();

    /// <summary>Explorer broadcasts this message when the taskbar is (re)created; icons must be added again.</summary>
    public static uint RegisterTaskbarCreatedMessage() => PInvoke.RegisterWindowMessage("TaskbarCreated");

    /// <summary>The large shell icon of a file; the caller owns it and must call <see cref="DestroyIcon"/>.</summary>
    public static nint ExtractFileIcon(string path)
    {
        var info = new SHFILEINFOW();
        nuint result;
        fixed (char* p = path)
            result = PInvoke.SHGetFileInfo(new PCWSTR(p), default, &info, (uint)sizeof(SHFILEINFOW), SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);
        return result == 0 ? 0 : (nint)info.hIcon.Value;
    }

    /// <summary>The stock application icon, shared with the system: do not destroy it.</summary>
    public static nint DefaultApplicationIcon()
    {
        const int IDI_APPLICATION = 32512;
        return (nint)PInvoke.LoadIcon(HINSTANCE.Null, new PCWSTR((char*)IDI_APPLICATION)).Value;
    }

    public static void DestroyIcon(nint icon)
    {
        if (icon != 0) PInvoke.DestroyIcon(new HICON(icon));
    }

    private static void Copy(string text, Span<char> target)
    {
        target.Clear();
        int n = Math.Min(text.Length, target.Length - 1);
        text.AsSpan(0, n).CopyTo(target);
    }

    public void Dispose()
    {
        var data = Data();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
    }
}
