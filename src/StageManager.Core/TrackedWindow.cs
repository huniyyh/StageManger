namespace StageManager.Core;

/// <summary>Per-window bookkeeping owned by the engine.</summary>
public sealed class TrackedWindow
{
    public WindowInfo Info { get; internal set; }
    public Snapshot? Snapshot { get; internal set; }

    /// <summary>When <see cref="Snapshot"/> was taken, on the engine's clock.</summary>
    public DateTimeOffset? SnapshotTakenAt { get; internal set; }

    /// <summary>Bounds before the engine touched the window; restored on Disable.</summary>
    public RectPx? OriginalBounds { get; internal set; }

    /// <summary>Where the window lives while its stage is active.</summary>
    public RectPx? StageBounds { get; internal set; }

    /// <summary>Where the window was when the user started dragging it; it returns there if the drag ends on the strip.</summary>
    public RectPx? DragStartBounds { get; internal set; }

    /// <summary>True while the window is minimized because the engine parked it.</summary>
    public bool ParkedByUs { get; internal set; }

    /// <summary>True once the engine moved the window, so Disable knows to put it back.</summary>
    public bool MovedByUs { get; internal set; }

    /// <summary>True after the window has been laid out on stage at least once.</summary>
    public bool Presented { get; internal set; }

    public TrackedWindow(WindowInfo info) => Info = info;
}
