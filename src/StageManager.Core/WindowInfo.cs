namespace StageManager.Core;

/// <summary>A point-in-time description of a manageable top-level window.</summary>
public sealed record WindowInfo(
    WindowId Id,
    string Title,
    string ClassName,
    uint ProcessId,
    string ProcessName,
    string? ExecutablePath,
    RectPx Bounds,
    bool IsMinimized,
    bool IsMaximized);
