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
/// and events arrive on that same thread. <see cref="CaptureSnapshot"/> may be called from any thread.
/// </summary>
public sealed unsafe class Win32WindowSystem : IWindowSystem, IDisposable
{
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
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

    /// <summary>Raw hook callbacks received, before any filtering. A cheap gauge of how busy the hooks make us.</summary>
    public long RawEventCount { get; private set; }

    /// <summary>Events that passed the filters and were forwarded.</summary>
    public long ForwardedEventCount { get; private set; }

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

    public PointPx GetCursorPosition() => NativeWindow.GetCursorPosition();

    private Microsoft.Win32.RegistryKey? _desktopsKey;
    private bool _desktopsKeyMissing;

    /// <summary>
    /// The shell records the current virtual desktop under HKCU; there is no public API or event for it.
    /// Reading the value is cheap enough to do on every tick and window event.
    /// </summary>
    public Guid GetCurrentDesktop()
    {
        if (_desktopsKey == null && !_desktopsKeyMissing)
        {
            _desktopsKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            _desktopsKeyMissing = _desktopsKey == null;
        }
        if (_desktopsKey?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } bytes)
            return new Guid(bytes);
        return Guid.Empty;
    }

    public RectPx? GetRestoredBounds(WindowId id)
    {
        var hwnd = ToHwnd(id);
        if (!PInvoke.IsIconic(hwnd))
            return PInvoke.GetWindowRect(hwnd, out RECT current) ? ToRect(current) : null;

        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!PInvoke.GetWindowPlacement(hwnd, ref placement)) return null;

        // rcNormalPosition is in workspace coordinates: relative to the work area origin, which differs from
        // the screen origin when the taskbar sits at the top or the left.
        var r = placement.rcNormalPosition;
        var workArea = GetPrimaryWorkArea();
        return new RectPx(r.left + workArea.Left, r.top + workArea.Top, r.right + workArea.Left, r.bottom + workArea.Top);
    }

    // ---------------------------------------------------------------- commands

    public void Minimize(WindowId id) => PInvoke.ShowWindow(ToHwnd(id), SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);

    public void SetTransitionsEnabled(WindowId id, bool enabled)
    {
        int disabled = enabled ? 0 : 1;
        PInvoke.DwmSetWindowAttribute(ToHwnd(id), DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disabled, sizeof(int));
    }

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

    /// <summary>A memory DC with a top-down 32bpp DIB, kept between captures so no 8MB surface is allocated per picture.</summary>
    private struct CaptureSurface
    {
        public HDC Dc;
        public HBITMAP Bitmap;
        public void* Bits;
        public int Width;
        public int Height;

        public readonly bool Fits(int w, int h) => !Bitmap.IsNull && w <= Width && h <= Height;
        public readonly int Stride => Width * 4;
    }

    private readonly object _captureLock = new();
    private CaptureSurface _full;  // the window at full size
    private CaptureSurface _small; // a scaled-down copy

    /// <summary>
    /// PrintWindow asks the window to render itself, which works even when it is covered and, under DWM, is
    /// about twice as fast as copying the screen. The screen copy remains as the fallback for windows that
    /// render nothing, and as an explicit option.
    /// </summary>
    public Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight, int thumbnailWidth = 0, int thumbnailHeight = 0, bool fromScreen = false)
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

        lock (_captureLock)
        {
            HDC screen = PInvoke.GetDC(HWND.Null);
            if (screen.IsNull) return null;
            try
            {
                if (!EnsureSurface(ref _full, screen, w, h)) return null;
                bool ok = !fromScreen && PInvoke.PrintWindow(hwnd, _full.Dc, (PRINT_WINDOW_FLAGS)PW_RENDERFULLCONTENT);
                if (!ok || LooksBlank(_full, w, h))
                    PInvoke.BitBlt(_full.Dc, 0, 0, w, h, screen, rc.left, rc.top, ROP_CODE.SRCCOPY);

                var picture = CopyOut(screen, crop, maxWidth, maxHeight);
                if (picture == null) return null;
                PixelBuffer? thumbnail = null;
                if (thumbnailWidth > 0 && thumbnailHeight > 0 && (picture.Width > thumbnailWidth || picture.Height > thumbnailHeight))
                    thumbnail = CopyOut(screen, crop, thumbnailWidth, thumbnailHeight);
                var insets = new Insets(crop.Left, crop.Top, w - crop.Right, h - crop.Bottom);
                return new Snapshot(picture.Width, picture.Height, picture.Bgra!, DateTimeOffset.UtcNow, insets, thumbnail);
            }
            finally { PInvoke.ReleaseDC(HWND.Null, screen); }
        }
    }

    /// <summary>Makes sure the surface can hold w x h pixels, growing it when needed and keeping it otherwise.</summary>
    private static bool EnsureSurface(ref CaptureSurface s, HDC screen, int w, int h)
    {
        if (s.Fits(w, h)) return true;
        if (s.Dc.IsNull)
        {
            s.Dc = PInvoke.CreateCompatibleDC(screen);
            if (s.Dc.IsNull) return false;
        }

        int width = Math.Max(w, s.Width), height = Math.Max(h, s.Height);
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        void* bits = null;
        var bitmap = PInvoke.CreateDIBSection(screen, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (bitmap.IsNull || bits == null) return false;

        PInvoke.SelectObject(s.Dc, bitmap);
        if (!s.Bitmap.IsNull) PInvoke.DeleteObject(s.Bitmap); // the old one is no longer selected
        s.Bitmap = bitmap;
        s.Bits = bits;
        s.Width = width;
        s.Height = height;
        return true;
    }

    private static bool LooksBlank(in CaptureSurface s, int w, int h)
    {
        byte* pixels = (byte*)s.Bits;
        int stride = s.Stride;
        int stepX = Math.Max(1, w / 48), stepY = Math.Max(1, h / 40); // roughly 2000 samples within the window's area
        for (int y = 0; y < h; y += stepY)
        {
            byte* row = pixels + (long)y * stride;
            for (int x = 0; x < w; x += stepX)
            {
                byte* px = row + x * 4;
                if (px[0] != 0 || px[1] != 0 || px[2] != 0) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Copies the cropped region of the full surface out into a managed array: straight from the surface when it
    /// fits <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, otherwise scaled down first with GDI's
    /// halftone (box) filter. Alpha is left as GDI wrote it; consumers read the pixels as Bgr32 and ignore it.
    /// </summary>
    private PixelBuffer? CopyOut(HDC screen, RectPx crop, int maxWidth, int maxHeight)
    {
        int cw = crop.Width, ch = crop.Height;
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / cw, (double)maxHeight / ch));
        int dw = Math.Max(1, (int)(cw * scale)), dh = Math.Max(1, (int)(ch * scale));

        byte* src;
        int srcStride;
        if (dw == cw && dh == ch)
        {
            src = (byte*)_full.Bits + (long)crop.Top * _full.Stride + crop.Left * 4;
            srcStride = _full.Stride;
        }
        else
        {
            if (!EnsureSurface(ref _small, screen, dw, dh)) return null;
            PInvoke.SetStretchBltMode(_small.Dc, STRETCH_BLT_MODE.HALFTONE);
            PInvoke.SetBrushOrgEx(_small.Dc, 0, 0, null);
            PInvoke.StretchBlt(_small.Dc, 0, 0, dw, dh, _full.Dc, crop.Left, crop.Top, cw, ch, ROP_CODE.SRCCOPY);
            src = (byte*)_small.Bits;
            srcStride = _small.Stride;
        }

        var dst = new byte[dw * dh * 4];
        int rowBytes = dw * 4;
        fixed (byte* d = dst)
        {
            for (int y = 0; y < dh; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, d + (long)y * rowBytes, rowBytes, rowBytes);
        }
        return new PixelBuffer(dw, dh, dst);
    }

    private static void ReleaseSurface(ref CaptureSurface s)
    {
        if (!s.Bitmap.IsNull) PInvoke.DeleteObject(s.Bitmap);
        if (!s.Dc.IsNull) PInvoke.DeleteDC(s.Dc);
        s = default;
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
        RawEventCount++;
        if (hwnd.IsNull || idObject != 0 || idChild != 0) return; // OBJID_WINDOW / CHILDID_SELF only

        WindowEventKind kind;
        switch (eventId)
        {
            case EVENT_SYSTEM_FOREGROUND: kind = WindowEventKind.Foreground; break;
            case EVENT_SYSTEM_MINIMIZESTART: kind = WindowEventKind.MinimizeStarted; break;
            case EVENT_SYSTEM_MINIMIZEEND: kind = WindowEventKind.MinimizeEnded; break;
            case EVENT_SYSTEM_MOVESIZESTART: kind = WindowEventKind.MoveSizeStarted; break;
            case EVENT_SYSTEM_MOVESIZEEND: kind = WindowEventKind.MoveSizeEnded; break;
            case EVENT_OBJECT_SHOW: kind = WindowEventKind.Shown; break;
            case EVENT_OBJECT_HIDE: kind = WindowEventKind.Hidden; break;
            case EVENT_OBJECT_DESTROY: kind = WindowEventKind.Destroyed; break;
            case EVENT_OBJECT_NAMECHANGE: kind = WindowEventKind.TitleChanged; break;
            case EVENT_OBJECT_LOCATIONCHANGE: kind = WindowEventKind.LocationChanged; break;
            case EVENT_OBJECT_CLOAKED: kind = WindowEventKind.Cloaked; break;
            case EVENT_OBJECT_UNCLOAKED: kind = WindowEventKind.Uncloaked; break;
            default: return;
        }

        // Only top-level windows are interesting. A destroyed window can no longer answer, so let it through.
        if (kind != WindowEventKind.Destroyed && PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) != hwnd) return;

        try
        {
            ForwardedEventCount++;
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
        _desktopsKey?.Dispose();
        _desktopsKey = null;
        lock (_captureLock)
        {
            ReleaseSurface(ref _full);
            ReleaseSurface(ref _small);
        }
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
