namespace StageManager.Core;

/// <summary>A window taking part in a stage swap: where it is (outgoing) or where it will end up (incoming), and its picture.</summary>
public sealed record SwapWindow(WindowId Id, RectPx Bounds, Snapshot? Snapshot, bool IsPrimary);

/// <summary>Where a window of the incoming stage will be put, computed before anything is touched.</summary>
internal sealed record PlannedPlacement(WindowId Id, RectPx Bounds, bool Apply);

/// <summary>
/// A prepared stage switch. Nothing has been touched when <see cref="StageEngine.PrepareSwap"/> returns it;
/// the caller commits the two halves in order, typically with an animation in between:
/// <see cref="StageEngine.CommitPark"/> hides the outgoing stage, <see cref="StageEngine.CommitPresent"/> shows the incoming one.
/// </summary>
public sealed class StageSwap
{
    public Stage? From { get; }
    public Stage To { get; }

    /// <summary>Visible windows of the outgoing stage with fresh snapshots.</summary>
    public IReadOnlyList<SwapWindow> Outgoing { get; }

    /// <summary>Windows of the incoming stage with the bounds they will be given and the snapshot taken when they were parked.</summary>
    public IReadOnlyList<SwapWindow> Incoming { get; }

    /// <summary>When true, the OS minimize and restore transitions are turned off for the windows involved.</summary>
    public bool SuppressTransitions { get; }

    public bool IsParked { get; internal set; }
    public bool IsPresented { get; internal set; }

    internal IReadOnlyList<PlannedPlacement> Plan { get; }

    internal StageSwap(Stage? from, Stage to, IReadOnlyList<SwapWindow> outgoing, IReadOnlyList<SwapWindow> incoming,
        IReadOnlyList<PlannedPlacement> plan, bool suppressTransitions)
    {
        From = from;
        To = to;
        Outgoing = outgoing;
        Incoming = incoming;
        Plan = plan;
        SuppressTransitions = suppressTransitions;
    }
}
