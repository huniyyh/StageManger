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

    void Minimize(WindowId id);
    void RestoreNoActivate(WindowId id);
    void SetBounds(WindowId id, RectPx bounds);
    bool Activate(WindowId id);

    /// <summary>Captures a thumbnail of a non-minimized window. Returns null when nothing could be captured.</summary>
    Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight);

    event Action<WindowEvent>? WindowChanged;
}
