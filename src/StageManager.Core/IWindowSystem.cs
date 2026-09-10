namespace StageManager.Core;

/// <summary>Everything the engine needs from the platform. Implemented with Win32 in production and faked in tests.</summary>
public interface IWindowSystem
{
    /// <summary>Manageable top-level windows in Z order, topmost first.</summary>
    IReadOnlyList<WindowInfo> EnumerateManageableWindows();

    /// <summary>Current facts about a window, or null when it no longer exists or is no longer manageable.</summary>
    WindowInfo? GetWindowInfo(WindowId id);

    WindowId? GetForegroundWindow();

    /// <summary>Work area of the primary monitor in physical pixels.</summary>
    RectPx GetPrimaryWorkArea();

    /// <summary>The bounds a window will have once it is no longer minimized, or its current bounds when it is not minimized.</summary>
    RectPx? GetRestoredBounds(WindowId id);

    /// <summary>The mouse pointer position in physical pixels.</summary>
    PointPx GetCursorPosition();

    /// <summary>Identity of the virtual desktop the user is looking at; <see cref="Guid.Empty"/> when unknown.</summary>
    Guid GetCurrentDesktop();

    void Minimize(WindowId id);
    void RestoreNoActivate(WindowId id);
    void SetBounds(WindowId id, RectPx bounds);
    bool Activate(WindowId id);

    /// <summary>Turns the OS minimize and restore animations of one window off or back on.</summary>
    void SetTransitionsEnabled(WindowId id, bool enabled);

    /// <summary>
    /// Captures a thumbnail of a non-minimized window. Returns null when nothing could be captured.
    /// With <paramref name="fromScreen"/> the pixels are copied straight off the screen, which is much faster
    /// and exact for a window that is fully visible, but includes anything drawn over it.
    /// </summary>
    Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight, bool fromScreen = false);

    event Action<WindowEvent>? WindowChanged;
}
