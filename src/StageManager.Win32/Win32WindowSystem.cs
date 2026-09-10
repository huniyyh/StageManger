using StageManager.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StageManager.Win32;

/// <summary>
/// Win32 implementation of <see cref="IWindowSystem"/>. Window events are delivered through an
/// out-of-context WinEvent hook, so <see cref="StartListening"/> must be called on a thread that pumps messages
/// and events arrive on that same thread.
/// </summary>
public sealed unsafe class Win32WindowSystem : IWindowSystem, IDisposable
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const byte VK_MENU = 0x12;

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, (string Name, string? Path)> _processCache = new();
    private readonly List<HWINEVENTHOOK> _hooks = new();
    private WINEVENTPROC? _hookProc; // must stay referenced for as long as the hooks live

    public event Action<WindowEvent>? WindowChanged;

    public Win32WindowSystem(Action<string>? log = null) => _log = log;

    // ---------------------------------------------------------------- queries

    public IReadOnlyList<WindowInfo> EnumerateManageableWindows()
    {
        var list = new List<WindowInfo>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            var info = Probe(hwnd);
            if (info != null) list.Add(info);
            return true;
        }, default);
        return list;
    }

    public WindowInfo? GetWindowInfo(WindowId id) => Probe(ToHwnd(id));

    public WindowId? GetForegroundWindow()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        return hwnd.IsNull ? null : ToId(hwnd);
    }

    public RectPx GetPrimaryWorkArea()
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (PInvoke.GetMonitorInfo(monitor, ref mi)) return ToRect(mi.rcWork);
        return new RectPx(0, 0, 1920, 1080);
    }

    // ---------------------------------------------------------------- commands

    public void Minimize(WindowId id) => PInvoke.ShowWindow(ToHwnd(id), SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);

    public void RestoreNoActivate(WindowId id) => PInvoke.ShowWindow(ToHwnd(id), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

    public void SetBounds(WindowId id, RectPx b)
        => PInvoke.SetWindowPos(ToHwnd(id), HWND.Null, b.Left, b.Top, b.Width, b.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    public bool Activate(WindowId id)
    {
        var hwnd = ToHwnd(id);
        if (PInvoke.IsIconic(hwnd)) PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        if (PInvoke.SetForegroundWindow(hwnd)) return true;

        // Foreground lock: a synthetic Alt press makes the system treat us as the input owner.
        PInvoke.keybd_event(VK_MENU, 0, default, 0);
        PInvoke.keybd_event(VK_MENU, 0, KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, 0);
        bool ok = PInvoke.SetForegroundWindow(hwnd);
        if (!ok) _log?.Invoke($"SetForegroundWindow failed for {id}");
        return ok;
    }

    // ---------------------------------------------------------------- snapshot

    public Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight)
    {
        var hwnd = ToHwnd(id);
        if (!PInvoke.IsWindow(hwnd) || PInvoke.IsIconic(hwnd)) return null;
        if (!PInvoke.GetWindowRect(hwnd, out RECT rc)) return null;
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        if (w <= 0 || h <= 0) return null;

        // DWM's visible frame excludes the invisible resize borders; crop to it.
        RECT frame;
        if (!PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Succeeded)
            frame = rc;
        var crop = new RectPx(
            Math.Max(0, frame.left - rc.left), Math.Max(0, frame.top - rc.top),
            Math.Min(w, frame.right - rc.left), Math.Min(h, frame.bottom - rc.top));
        if (crop.Width <= 0 || crop.Height <= 0) crop = new RectPx(0, 0, w, h);

        HDC screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull) return null;
        try
        {
            var mem = PInvoke.CreateCompatibleDC(screen);
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h; // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                void* bits = null;
                var bmp = PInvoke.CreateDIBSection(screen, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
                if (bmp.IsNull || bits == null) return null;
                try
                {
                    var previous = PInvoke.SelectObject(mem, bmp);
                    bool ok = PInvoke.PrintWindow(hwnd, mem, (PRINT_WINDOW_FLAGS)PW_RENDERFULLCONTENT);
                    var pixels = new ReadOnlySpan<byte>(bits, w * h * 4);
                    if (!ok || LooksBlank(pixels))
                    {
                        // Fallback: copy what is on screen. Good enough for the foreground window we are about to park.
                        PInvoke.BitBlt(mem, 0, 0, w, h, screen, rc.left, rc.top, ROP_CODE.SRCCOPY);
                    }
                    var snapshot = Downscale(pixels, w, crop, maxWidth, maxHeight);
                    PInvoke.SelectObject(mem, previous);
                    return snapshot;
                }
                finally { PInvoke.DeleteObject(bmp); }
            }
            finally { PInvoke.DeleteDC(mem); }
        }
        finally { PInvoke.ReleaseDC(HWND.Null, screen); }
    }

    private static bool LooksBlank(ReadOnlySpan<byte> bgra)
    {
        int step = Math.Max(4, bgra.Length / 4 / 2000 * 4); // roughly 2000 samples
        for (int i = 0; i + 2 < bgra.Length; i += step)
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) return false;
        return true;
    }

    /// <summary>Box-filter downscale of a cropped region of a top-down BGRA32 bitmap.</summary>
    private static Snapshot Downscale(ReadOnlySpan<byte> src, int srcWidth, RectPx crop, int maxWidth, int maxHeight)
    {
        int cw = crop.Width, ch = crop.Height;
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / cw, (double)maxHeight / ch));
        int dw = Math.Max(1, (int)(cw * scale)), dh = Math.Max(1, (int)(ch * scale));
        var dst = new byte[dw * dh * 4];

        for (int y = 0; y < dh; y++)
        {
            int sy0 = crop.Top + y * ch / dh;
            int sy1 = crop.Top + Math.Max(y * ch / dh + 1, (y + 1) * ch / dh);
            for (int x = 0; x < dw; x++)
            {
                int sx0 = crop.Left + x * cw / dw;
                int sx1 = crop.Left + Math.Max(x * cw / dw + 1, (x + 1) * cw / dw);
                long b = 0, g = 0, r = 0;
                int n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                {
                    int row = sy * srcWidth * 4;
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int i = row + sx * 4;
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                        n++;
                    }
                }
                int o = (y * dw + x) * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = 255;
            }
        }
        return new Snapshot(dw, dh, dst, DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------------------- events

    /// <summary>Installs the WinEvent hooks on the calling thread. That thread must pump messages.</summary>
    public void StartListening()
    {
        if (_hooks.Count > 0) return;
        _hookProc = HookCallback;
        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(PInvoke.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, HINSTANCE.Null, _hookProc, 0, 0, flags));
        _hooks.Add(PInvoke.SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_UNCLOAKED, HINSTANCE.Null, _hookProc, 0, 0, flags));
    }

    private void HookCallback(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (hwnd.IsNull || idObject != 0 || idChild != 0) return; // OBJID_WINDOW / CHILDID_SELF only

        WindowEventKind kind;
        switch (eventId)
        {
            case EVENT_SYSTEM_FOREGROUND: kind = WindowEventKind.Foreground; break;
            case EVENT_SYSTEM_MINIMIZESTART: kind = WindowEventKind.MinimizeStarted; break;
            case EVENT_SYSTEM_MINIMIZEEND: kind = WindowEventKind.MinimizeEnded; break;
            case EVENT_SYSTEM_MOVESIZEEND: kind = WindowEventKind.MoveSizeEnded; break;
            case EVENT_OBJECT_SHOW: kind = WindowEventKind.Shown; break;
            case EVENT_OBJECT_HIDE: kind = WindowEventKind.Hidden; break;
            case EVENT_OBJECT_DESTROY: kind = WindowEventKind.Destroyed; break;
            case EVENT_OBJECT_NAMECHANGE: kind = WindowEventKind.TitleChanged; break;
            case EVENT_OBJECT_CLOAKED: kind = WindowEventKind.Cloaked; break;
            case EVENT_OBJECT_UNCLOAKED: kind = WindowEventKind.Uncloaked; break;
            default: return;
        }

        // Only top-level windows are interesting. A destroyed window can no longer answer, so let it through.
        if (kind != WindowEventKind.Destroyed && PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd) return;

        try
        {
            WindowChanged?.Invoke(new WindowEvent(kind, ToId(hwnd)));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"event handler failed: {ex}");
        }
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) PInvoke.UnhookWinEvent(hook);
        _hooks.Clear();
        _hookProc = null;
    }

    // ---------------------------------------------------------------- probing

    private WindowInfo? Probe(HWND hwnd)
    {
        if (hwnd.IsNull || !PInvoke.IsWindow(hwnd) || !PInvoke.IsWindowVisible(hwnd)) return null;

        uint style = unchecked((uint)(long)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE));
        uint exStyle = unchecked((uint)(long)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE));
        string className = GetClassName(hwnd);
        string title = GetTitle(hwnd);

        uint framePid = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &framePid);
        uint pid = className == "ApplicationFrameWindow" ? FindUwpProcessId(hwnd, framePid) : framePid;
        var (processName, exePath) = GetProcess(pid);

        var probe = new WindowProbe(
            IsVisible: true,
            IsChild: (style & WS_CHILD) != 0,
            IsToolWindow: (exStyle & WS_EX_TOOLWINDOW) != 0,
            IsAppWindow: (exStyle & WS_EX_APPWINDOW) != 0,
            IsNoActivate: (exStyle & WS_EX_NOACTIVATE) != 0,
            IsCloaked: IsCloaked(hwnd),
            IsRootOwner: PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) == hwnd,
            IsOwnProcess: framePid == _ownPid,
            Title: title,
            ClassName: className,
            ProcessName: processName);
        if (!WindowFilter.IsManageable(probe)) return null;

        PInvoke.GetWindowRect(hwnd, out RECT rc);
        return new WindowInfo(ToId(hwnd), title, className, pid, processName, exePath, ToRect(rc),
            PInvoke.IsIconic(hwnd), PInvoke.IsZoomed(hwnd));
    }

    /// <summary>UWP windows are hosted by ApplicationFrameHost; the real app owns the CoreWindow child.</summary>
    private static uint FindUwpProcessId(HWND frame, uint fallback)
    {
        uint found = 0;
        PInvoke.EnumChildWindows(frame, (child, _) =>
        {
            if (GetClassName(child) != "Windows.UI.Core.CoreWindow") return true;
            uint pid = 0;
            PInvoke.GetWindowThreadProcessId(child, &pid);
            found = pid;
            return false;
        }, default);
        return found != 0 ? found : fallback;
    }

    private static bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return cloaked != 0;
    }

    private static string GetTitle(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        fixed (char* p = buffer)
        {
            int n = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return n > 0 ? new string(buffer[..n]) : string.Empty;
        }
    }

    private static string GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* p = buffer)
        {
            int n = PInvoke.GetClassName(hwnd, p, buffer.Length);
            return n > 0 ? new string(buffer[..n]) : string.Empty;
        }
    }

    private (string Name, string? Path) GetProcess(uint pid)
    {
        if (_processCache.TryGetValue(pid, out var cached)) return cached;

        string name = "?";
        string? path = null;
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (!handle.IsNull)
        {
            try
            {
                Span<char> buffer = stackalloc char[1024];
                uint size = (uint)buffer.Length;
                fixed (char* p = buffer)
                {
                    if (PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, p, &size))
                    {
                        path = new string(buffer[..(int)size]);
                        name = System.IO.Path.GetFileNameWithoutExtension(path);
                    }
                }
            }
            finally { PInvoke.CloseHandle(handle); }
        }

        if (_processCache.Count > 512) _processCache.Clear();
        _processCache[pid] = (name, path);
        return (name, path);
    }

    private static HWND ToHwnd(WindowId id) => new(id.Value);
    private static WindowId ToId(HWND hwnd) => new((nint)hwnd.Value);
    private static RectPx ToRect(RECT r) => new(r.left, r.top, r.right, r.bottom);
}
