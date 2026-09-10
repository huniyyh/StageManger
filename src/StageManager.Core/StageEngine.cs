namespace StageManager.Core;

/// <summary>
/// The Stage Manager state machine. Exactly one stage is active and laid out in the center of the screen;
/// every other stage is parked (its windows minimized) and represented by a thumbnail in the strip.
/// All members must be called from a single thread.
/// </summary>
public sealed class StageEngine
{
    public const int SnapshotMaxWidth = 800;
    public const int SnapshotMaxHeight = 600;

    /// <summary>How long a newly shown background window may wait to become foreground before it is parked.</summary>
    public static readonly TimeSpan PendingDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after a window is first placed on stage we check whether its app moved it on its own.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How many times a self-moving window is put back before we give up.</summary>
    public const int MaxSettleAttempts = 2;

    /// <summary>A snapshot of a visible window younger than this is reused by <see cref="PrepareSwap"/> instead of being retaken.</summary>
    public static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMilliseconds(600);

    /// <summary>How long OS transitions stay off after we hid or showed a window without one.</summary>
    public static readonly TimeSpan TransitionRestoreDelay = TimeSpan.FromMilliseconds(500);

    private readonly IWindowSystem _ws;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly List<Stage> _stages = new();
    private readonly Dictionary<WindowId, TrackedWindow> _windows = new();
    private readonly Dictionary<WindowId, DateTimeOffset> _pending = new();
    private readonly Dictionary<WindowId, (DateTimeOffset Due, int Attempts)> _settling = new();
    private readonly Dictionary<WindowId, DateTimeOffset> _transitionRestore = new();
    private StageSwap? _pendingSwap;
    private WindowId? _userDragging;

    public LayoutSettings Layout { get; set; }
    public bool IsEnabled { get; private set; }
    public Stage? ActiveStage { get; private set; }

    /// <summary>All stages, most recently used first.</summary>
    public IReadOnlyList<Stage> Stages => _stages;

    /// <summary>Stages shown in the strip: everything except the active one, most recent first.</summary>
    public IEnumerable<Stage> StripStages => _stages.Where(s => s != ActiveStage);

    /// <summary>Windows currently minimized by the engine; persisted so a crash can be undone.</summary>
    public IReadOnlyList<WindowId> ParkedByUs => _windows.Where(kv => kv.Value.ParkedByUs).Select(kv => kv.Key).ToList();

    public event Action? Changed;

    /// <summary>Raised with true when the user starts dragging a window of the active stage, and with false when the drag ends.</summary>
    public event Action<bool>? UserDragChanged;

    /// <summary>
    /// Raised when the user drops a window of the active stage on the strip. A subscriber is expected to call
    /// <see cref="CommitDetach"/>, typically after <see cref="PrepareDetach"/> and an animation; with no subscriber the engine detaches immediately.
    /// </summary>
    public event Action<WindowId>? DroppedOnStrip;

    public StageEngine(IWindowSystem ws, LayoutSettings? layout = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _ws = ws;
        Layout = layout ?? new LayoutSettings();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    public TrackedWindow? GetWindow(WindowId id) => _windows.GetValueOrDefault(id);

    public Stage? FindStageOf(WindowId id) => _stages.FirstOrDefault(s => s.Windows.Contains(id));

    // ---------------------------------------------------------------- lifecycle

    public void Enable()
    {
        if (IsEnabled) return;
        Reset();
        IsEnabled = true;

        var fg = _ws.GetForegroundWindow();
        foreach (var info in _ws.EnumerateManageableWindows())
        {
            if (_windows.ContainsKey(info.Id)) continue;
            // A minimized window reports a bogus off-screen rectangle; its real bounds are learned when it is restored.
            _windows[info.Id] = new TrackedWindow(info) { OriginalBounds = info.IsMinimized ? null : info.Bounds };
            Attach(FindStageByProcess(info.ProcessId) ?? CreateStage(info, atEnd: true), info.Id);
        }

        Stage? active = fg is { } f ? FindStageOf(f) : null;
        active ??= _stages.FirstOrDefault(s => s.Windows.Any(w => !_windows[w].Info.IsMinimized));

        foreach (var s in _stages)
            if (s != active) Park(s);

        if (active != null)
        {
            if (fg is { } f2 && active.Windows.Contains(f2)) active.Primary = f2;
            ActiveStage = active;
            ApplyPresentation(active, PlanPresentation(active), activate: false, suppressTransitions: false);
            MoveToFront(active);
        }

        Log($"enabled: {_stages.Count} stage(s), active={active?.ToString() ?? "none"}");
        RaiseChanged();
    }

    public void Disable()
    {
        if (!IsEnabled) return;
        foreach (var (id, w) in _windows)
        {
            if (w.ParkedByUs)
            {
                _ws.RestoreNoActivate(id);
                w.ParkedByUs = false;
            }
            if (!w.MovedByUs || w.OriginalBounds is not { } original) continue;
            var info = _ws.GetWindowInfo(id);
            if (info is { IsMinimized: false, IsMaximized: false } && info.Bounds != original)
                _ws.SetBounds(id, original);
        }
        foreach (var id in _transitionRestore.Keys) _ws.SetTransitionsEnabled(id, true);
        Reset();
        IsEnabled = false;
        Log("disabled");
        RaiseChanged();
    }

    /// <summary>Brings a stage to the center immediately; the previously active stage is parked.</summary>
    public void ActivateStage(Stage stage)
    {
        if (!IsEnabled || !_stages.Contains(stage)) return;
        if (stage == ActiveStage)
        {
            FinishPendingSwap();
            if (stage.Primary is { } p) _ws.Activate(p);
            return;
        }

        var swap = PrepareSwap(stage);
        if (swap == null) return;
        Log($"activate {stage}");
        CommitPresent(swap);
    }

    // ---------------------------------------------------------------- swaps in two halves

    /// <summary>
    /// Plans a switch to <paramref name="to"/> without touching any window: takes fresh snapshots of the outgoing
    /// windows and computes where the incoming ones will go. Returns null when there is nothing to switch.
    /// Any swap still pending is finished first.
    /// </summary>
    public StageSwap? PrepareSwap(Stage to, bool suppressTransitions = false)
    {
        if (!IsEnabled || !_stages.Contains(to) || to == ActiveStage) return null;
        FinishPendingSwap();

        var from = ActiveStage;
        var outgoing = new List<SwapWindow>();
        if (from != null)
        {
            foreach (var id in from.Windows)
            {
                if (!_windows.TryGetValue(id, out var w)) continue;
                var info = _ws.GetWindowInfo(id);
                if (info == null || info.IsMinimized) continue;
                w.Info = info;
                // The outgoing windows are on screen right now, so a screen copy is exact; a fresh prefetched one is reused.
                if (!IsFresh(w, SnapshotFreshness)) CaptureInto(w, fromScreen: true);
                outgoing.Add(new SwapWindow(id, info.Bounds, w.Snapshot, id == from.Primary));
            }
        }

        var plan = PlanPresentation(to);
        var primary = ResolvePrimary(to);
        var incoming = plan
            .Select(p => new SwapWindow(p.Id, p.Bounds, _windows.GetValueOrDefault(p.Id)?.Snapshot, p.Id == primary))
            .ToList();

        _pendingSwap = new StageSwap(from, to, outgoing, incoming, plan, suppressTransitions);
        Log($"swap {from?.ToString() ?? "none"} -> {to}: {outgoing.Count} out, {incoming.Count} in");
        return _pendingSwap;
    }

    /// <summary>First half of a swap: the outgoing stage is parked and <see cref="ActiveStage"/> becomes the target.</summary>
    public void CommitPark(StageSwap swap)
    {
        if (!IsCurrent(swap) || swap.IsParked) return;
        swap.IsParked = true;
        ActiveStage = swap.To;
        if (swap.From != null) Park(swap.From, captureSnapshots: false, swap.SuppressTransitions);
    }

    /// <summary>Second half of a swap: the incoming stage is restored, laid out and focused. Parks first if needed.</summary>
    public void CommitPresent(StageSwap swap)
    {
        if (!IsCurrent(swap)) return;
        CommitPark(swap);
        swap.IsPresented = true;
        _pendingSwap = null;

        ApplyPresentation(swap.To, swap.Plan, activate: true, swap.SuppressTransitions);
        if (swap.SuppressTransitions)
            foreach (var o in swap.Outgoing) _ws.SetTransitionsEnabled(o.Id, true);
        MoveToFront(swap.To);
        RaiseChanged();
    }

    /// <summary>
    /// Takes pictures of the active stage's visible windows ahead of a likely swap, so the swap itself does not
    /// have to wait for a capture. Call it when the pointer enters the strip. Cheap when the pictures are recent.
    /// </summary>
    public void PrefetchActiveSnapshots(TimeSpan maxAge)
    {
        if (!IsEnabled || ActiveStage == null) return;
        foreach (var id in ActiveStage.Windows)
        {
            if (!_windows.TryGetValue(id, out var w) || w.Info.IsMinimized || IsFresh(w, maxAge)) continue;
            CaptureInto(w, fromScreen: true);
        }
    }

    private bool IsFresh(TrackedWindow w, TimeSpan maxAge)
        => w.Snapshot != null && w.SnapshotTakenAt is { } at && _clock() - at < maxAge;

    private void CaptureInto(TrackedWindow w, bool fromScreen)
    {
        var snap = _ws.CaptureSnapshot(w.Info.Id, SnapshotMaxWidth, SnapshotMaxHeight, fromScreen);
        if (snap == null) return;
        w.Snapshot = snap;
        w.SnapshotTakenAt = _clock();
    }

    private bool IsCurrent(StageSwap swap) => IsEnabled && ReferenceEquals(_pendingSwap, swap);

    private void FinishPendingSwap()
    {
        if (_pendingSwap is { } swap) CommitPresent(swap);
    }

    // ---------------------------------------------------------------- grouping: merge and detach

    /// <summary>
    /// Adds every window of <paramref name="source"/> to the active stage, the way dragging a thumbnail onto the
    /// desktop does on macOS. With an <paramref name="anchor"/> the windows are centered on it (kept on screen);
    /// without one they keep their remembered places. With no active stage this simply activates the stage.
    /// </summary>
    public void MergeIntoActive(Stage source, PointPx? anchor, bool suppressTransitions = false)
    {
        if (!IsEnabled || !_stages.Contains(source) || source == ActiveStage) return;
        FinishPendingSwap();
        if (ActiveStage == null)
        {
            ActivateStage(source);
            return;
        }

        var target = ActiveStage;
        var available = StageLayout.AvailableArea(_ws.GetPrimaryWorkArea(), Layout);
        var primary = ResolvePrimary(source);
        Log($"merge {source} into {target}");

        foreach (var id in source.Windows.ToArray())
        {
            if (!_windows.TryGetValue(id, out var w)) continue;
            Detach(source, id);
            Attach(target, id);

            var info = _ws.GetWindowInfo(id);
            if (info == null) continue;
            if (suppressTransitions) SuppressTransitionsFor(id);
            if (info.IsMinimized)
            {
                _ws.RestoreNoActivate(id);
                w.ParkedByUs = false;
                info = _ws.GetWindowInfo(id) ?? (info with { IsMinimized = false });
            }
            w.OriginalBounds ??= info.Bounds;

            if (info.IsMaximized)
            {
                MarkPresented(w, info);
            }
            else if (anchor is { } a)
            {
                var size = w.StageBounds ?? info.Bounds;
                ApplyBounds(w, info, StageLayout.Place(size.CenteredAt(a), available, center: false));
            }
            else
            {
                PlaceWindow(w, info, available, center: false);
            }
        }

        _stages.Remove(source);
        if (primary is { } p)
        {
            _ws.Activate(p);
            target.Primary = p;
        }
        RaiseChanged();
    }

    /// <summary>Takes a picture of a window that is about to leave the active stage, without changing anything.</summary>
    public SwapWindow? PrepareDetach(WindowId id)
    {
        if (!IsEnabled || !_windows.TryGetValue(id, out var w)) return null;
        var stage = FindStageOf(id);
        if (stage == null || stage != ActiveStage) return null;
        var info = _ws.GetWindowInfo(id);
        if (info == null || info.IsMinimized) return null;
        w.Info = info;
        CaptureInto(w, fromScreen: false); // it may overlap the strip right now, so render it rather than copy the screen
        return new SwapWindow(id, info.Bounds, w.Snapshot, id == stage.Primary);
    }

    /// <summary>
    /// Takes a window out of the active stage and parks it in a stage of its own, the way dragging a window onto
    /// the strip does on macOS. It returns to where its drag began when it is brought back.
    /// </summary>
    public void CommitDetach(WindowId id, bool suppressTransitions = false)
    {
        if (!IsEnabled || !_windows.TryGetValue(id, out var w)) return;
        var stage = FindStageOf(id);
        if (stage == null || stage != ActiveStage) return;
        FinishPendingSwap();

        var restoreTo = w.DragStartBounds ?? w.StageBounds ?? w.Info.Bounds;
        w.DragStartBounds = null;
        _pending.Remove(id);
        _settling.Remove(id);

        Detach(stage, id); // may dissolve the stage and leave no active stage
        var own = new Stage(w.Info.ProcessId, w.Info.ProcessName);
        _stages.Insert(ActiveStage != null && _stages.Count > 0 && _stages[0] == ActiveStage ? 1 : 0, own);
        Attach(own, id);

        var info = _ws.GetWindowInfo(id);
        if (info != null && !info.IsMinimized)
        {
            w.Info = info;
            if (!IsFresh(w, SnapshotFreshness)) CaptureInto(w, fromScreen: false);
            if (suppressTransitions) SuppressTransitionsFor(id);
            w.ParkedByUs = true;
            _ws.Minimize(id);
            w.Info = info with { IsMinimized = true };
        }
        w.StageBounds = restoreTo;
        Log($"detach '{w.Info.Title}' to the strip");
        RaiseChanged();
    }

    private void SuppressTransitionsFor(WindowId id)
    {
        _ws.SetTransitionsEnabled(id, false);
        _transitionRestore[id] = _clock() + TransitionRestoreDelay;
    }

    /// <summary>
    /// Call periodically (a few times per second). Parks new windows that never became foreground, puts back
    /// windows whose app moved them right after we placed them, and turns OS transitions back on.
    /// </summary>
    public void Tick()
    {
        if (!IsEnabled) return;
        if (_pending.Count > 0) ProcessPending();
        if (_settling.Count > 0) ProcessSettling();
        if (_transitionRestore.Count > 0) ProcessTransitionRestore();
    }

    private void ProcessPending()
    {
        var now = _clock();
        foreach (var (id, since) in _pending.ToArray())
        {
            if (now - since < PendingDelay) continue;
            _pending.Remove(id);
            var stage = FindStageOf(id);
            if (stage == null || stage == ActiveStage) continue;
            if (_ws.GetForegroundWindow() == id)
            {
                stage.Primary = id;
                ActivateStage(stage);
                continue;
            }
            ParkWindow(id);
            RaiseChanged();
        }
    }

    private void ProcessSettling()
    {
        var now = _clock();
        foreach (var (id, entry) in _settling.ToArray())
        {
            if (now < entry.Due) continue;
            _settling.Remove(id);
            if (!_windows.TryGetValue(id, out var w)) continue;
            var stage = FindStageOf(id);
            if (stage == null || stage != ActiveStage) continue;
            var info = _ws.GetWindowInfo(id);
            if (info == null || info.IsMinimized || info.IsMaximized) continue;
            if (w.StageBounds is not { } expected || info.Bounds == expected) continue;

            // The app moved or resized itself after we placed it, the way browsers restore their saved placement.
            // Keep the size it chose but put it back where it belongs.
            Log($"re-place '{info.Title}': it moved itself to {info.Bounds}");
            var available = StageLayout.AvailableArea(_ws.GetPrimaryWorkArea(), Layout);
            PlaceWindow(w, info, available, center: stage.Windows.Count == 1, source: info.Bounds);
            if (entry.Attempts + 1 < MaxSettleAttempts)
                _settling[id] = (now + SettleDelay, entry.Attempts + 1);
        }
    }

    private void ProcessTransitionRestore()
    {
        var now = _clock();
        foreach (var (id, due) in _transitionRestore.ToArray())
        {
            if (now < due) continue;
            _transitionRestore.Remove(id);
            _ws.SetTransitionsEnabled(id, true);
        }
    }

    // ---------------------------------------------------------------- events

    public void OnWindowEvent(WindowEvent e)
    {
        if (!IsEnabled) return;
        switch (e.Kind)
        {
            case WindowEventKind.Foreground: OnForeground(e.Window); break;
            case WindowEventKind.Shown:
            case WindowEventKind.Uncloaked:
            case WindowEventKind.TitleChanged: OnShownOrChanged(e.Window); break;
            case WindowEventKind.Hidden:
            case WindowEventKind.Destroyed: RemoveWindow(e.Window); break;
            case WindowEventKind.Cloaked: break; // another virtual desktop is showing; the window and its stage live on
            case WindowEventKind.MinimizeStarted: OnMinimizeStarted(e.Window); break;
            case WindowEventKind.MinimizeEnded: OnMinimizeEnded(e.Window); break;
            case WindowEventKind.MoveSizeStarted: OnMoveSizeStarted(e.Window); break;
            case WindowEventKind.MoveSizeEnded: OnMoveSizeEnded(e.Window); break;
        }
    }

    private void OnForeground(WindowId id)
    {
        _pending.Remove(id);
        if (!_windows.ContainsKey(id))
        {
            var info = _ws.GetWindowInfo(id);
            if (info == null) return;
            AddWindow(info, isForeground: true);
            return;
        }

        var stage = FindStageOf(id);
        if (stage == null) return;
        stage.Primary = id;
        if (stage == ActiveStage) return;

        // The user switched to a parked stage through Alt-Tab or the taskbar; the shell already restored this window.
        _windows[id].ParkedByUs = false;
        ActivateStage(stage);
    }

    private void OnShownOrChanged(WindowId id)
    {
        var info = _ws.GetWindowInfo(id);
        // A known window that is not manageable right now is usually just cloaked on another virtual desktop;
        // it is removed when it is hidden or destroyed, not here.
        if (info == null) return;

        if (_windows.TryGetValue(id, out var w))
        {
            bool titleChanged = w.Info.Title != info.Title;
            w.Info = info;
            if (titleChanged) RaiseChanged();
            return;
        }

        AddWindow(info, isForeground: _ws.GetForegroundWindow() == id);
    }

    private void OnMinimizeStarted(WindowId id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        if (w.ParkedByUs) return; // our own doing
        var stage = FindStageOf(id);
        if (stage == null) return;

        // The user minimized the window: it goes to the strip, on its own if it left a multi-window stage.
        _pending.Remove(id);
        _settling.Remove(id);
        CaptureInto(w, fromScreen: false);
        w.StageBounds ??= w.Info.Bounds;
        w.Info = w.Info with { IsMinimized = true };

        if (stage == ActiveStage)
        {
            if (stage.Windows.Count == 1)
            {
                ActiveStage = null;
            }
            else
            {
                Detach(stage, id);
                var own = new Stage(w.Info.ProcessId, w.Info.ProcessName);
                _stages.Insert(Math.Min(1, _stages.Count), own);
                Attach(own, id);
            }
        }

        Log($"user minimized '{w.Info.Title}'");
        RaiseChanged();
    }

    private void OnMinimizeEnded(WindowId id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        var stage = FindStageOf(id);
        if (stage == null) return;
        w.Info = w.Info with { IsMinimized = false };
        if (stage == ActiveStage) return; // restored by us, or already handled by OnForeground

        // The user restored a parked window from the taskbar.
        w.ParkedByUs = false;
        stage.Primary = id;
        ActivateStage(stage);
    }

    private void OnMoveSizeStarted(WindowId id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        if (FindStageOf(id) != ActiveStage) return;
        var info = _ws.GetWindowInfo(id);
        if (info == null) return;
        w.Info = info;
        w.DragStartBounds = info.Bounds;
        _userDragging = id;
        UserDragChanged?.Invoke(true);
    }

    private void OnMoveSizeEnded(WindowId id)
    {
        bool wasDragging = _userDragging == id;
        if (wasDragging)
        {
            _userDragging = null;
            UserDragChanged?.Invoke(false);
        }

        if (!_windows.TryGetValue(id, out var w)) return;
        var info = _ws.GetWindowInfo(id);
        if (info == null) return;
        w.Info = info;
        _settling.Remove(id); // the user took over; never fight a drag
        if (FindStageOf(id) != ActiveStage) return;

        // A move (not a resize) that ends with the pointer on the strip sends the window there.
        bool moved = w.DragStartBounds is { } start
            && start.Width == info.Bounds.Width && start.Height == info.Bounds.Height && start != info.Bounds;
        var strip = StageLayout.StripArea(_ws.GetPrimaryWorkArea(), Layout);
        if (wasDragging && moved && strip.Contains(_ws.GetCursorPosition()))
        {
            if (DroppedOnStrip != null) DroppedOnStrip(id);
            else CommitDetach(id);
            return;
        }

        w.StageBounds = info.Bounds;
        w.DragStartBounds = null;
    }

    // ---------------------------------------------------------------- window bookkeeping

    private void AddWindow(WindowInfo info, bool isForeground)
    {
        var w = new TrackedWindow(info) { OriginalBounds = info.IsMinimized ? null : info.Bounds };
        _windows[info.Id] = w;
        var stage = FindStageByProcess(info.ProcessId) ?? CreateStage(info, atEnd: false);
        Attach(stage, info.Id);
        Log($"add '{info.Title}' -> {stage}");

        if (stage == ActiveStage)
        {
            if (!info.IsMinimized)
                PlaceWindow(w, info, StageLayout.AvailableArea(_ws.GetPrimaryWorkArea(), Layout), center: false);
            if (isForeground) stage.Primary = info.Id;
            RaiseChanged();
            return;
        }

        if (isForeground)
        {
            stage.Primary = info.Id;
            ActivateStage(stage);
            return;
        }

        if (!info.IsMinimized)
            _pending[info.Id] = _clock(); // give it a moment to become foreground before parking it
        RaiseChanged();
    }

    private void RemoveWindow(WindowId id)
    {
        _pending.Remove(id);
        _settling.Remove(id);
        _transitionRestore.Remove(id);
        if (_userDragging == id)
        {
            _userDragging = null;
            UserDragChanged?.Invoke(false);
        }
        if (!_windows.Remove(id, out var w)) return;

        var stage = FindStageOf(id);
        if (stage != null) Detach(stage, id);

        Log($"remove '{w.Info.Title}'");
        RaiseChanged();
    }

    private void Park(Stage stage, bool captureSnapshots = true, bool suppressTransitions = false)
    {
        foreach (var id in stage.Windows.ToArray()) ParkWindow(id, captureSnapshots, suppressTransitions);
    }

    private void ParkWindow(WindowId id, bool captureSnapshot = true, bool suppressTransitions = false)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        var info = _ws.GetWindowInfo(id);
        if (info == null) return;
        w.Info = info;
        _settling.Remove(id);
        if (info.IsMinimized) return;

        w.StageBounds = info.Bounds;
        if (captureSnapshot) CaptureInto(w, fromScreen: false);
        if (suppressTransitions) _ws.SetTransitionsEnabled(id, false);
        w.ParkedByUs = true; // set before minimizing so the resulting event is recognised as ours
        _ws.Minimize(id);
        w.Info = info with { IsMinimized = true };
    }

    /// <summary>Decides where each window of a stage goes when the stage is shown, without moving anything.</summary>
    private List<PlannedPlacement> PlanPresentation(Stage stage)
    {
        var workArea = _ws.GetPrimaryWorkArea();
        var available = StageLayout.AvailableArea(workArea, Layout);
        bool single = stage.Windows.Count == 1;
        var plan = new List<PlannedPlacement>();

        foreach (var id in stage.Windows)
        {
            if (!_windows.TryGetValue(id, out var w)) continue;
            var info = _ws.GetWindowInfo(id);
            if (info == null) continue;
            if (info.IsMaximized)
            {
                plan.Add(new PlannedPlacement(id, workArea, Apply: false));
                continue;
            }

            var current = info.IsMinimized ? _ws.GetRestoredBounds(id) ?? info.Bounds : info.Bounds;
            var source = w.StageBounds ?? current;
            bool center = single && !w.Presented;
            plan.Add(new PlannedPlacement(id, StageLayout.Place(source, available, center), Apply: true));
        }
        return plan;
    }

    private void ApplyPresentation(Stage stage, IReadOnlyList<PlannedPlacement> plan, bool activate, bool suppressTransitions)
    {
        foreach (var p in plan)
        {
            if (!_windows.TryGetValue(p.Id, out var w)) continue;
            var info = _ws.GetWindowInfo(p.Id);
            if (info == null) continue;

            if (suppressTransitions) _ws.SetTransitionsEnabled(p.Id, false);
            if (info.IsMinimized)
            {
                _ws.RestoreNoActivate(p.Id);
                w.ParkedByUs = false;
                info = _ws.GetWindowInfo(p.Id) ?? (info with { IsMinimized = false });
            }
            w.OriginalBounds ??= info.Bounds; // first time we see it un-minimized

            if (p.Apply && !info.IsMaximized) ApplyBounds(w, info, p.Bounds);
            else MarkPresented(w, info);
        }

        if (activate)
        {
            var primary = ResolvePrimary(stage);
            if (primary is { } id)
            {
                _ws.Activate(id);
                stage.Primary = id;
            }
        }

        if (suppressTransitions)
            foreach (var p in plan) _ws.SetTransitionsEnabled(p.Id, true);
    }

    /// <param name="source">Bounds to fit into the area; defaults to the remembered stage bounds, or the current bounds on first placement.</param>
    private void PlaceWindow(TrackedWindow w, WindowInfo info, RectPx available, bool center, RectPx? source = null)
    {
        if (info.IsMaximized)
        {
            MarkPresented(w, info);
            return;
        }
        var from = source ?? w.StageBounds ?? info.Bounds;
        ApplyBounds(w, info, StageLayout.Place(from, available, center));
    }

    private void ApplyBounds(TrackedWindow w, WindowInfo info, RectPx target)
    {
        if (target != info.Bounds)
        {
            _ws.SetBounds(info.Id, target);
            w.MovedByUs = true;
        }
        w.StageBounds = target;

        // Some apps reposition themselves right after they appear; check back once they have had a moment.
        if (!w.Presented) _settling[info.Id] = (_clock() + SettleDelay, 0);
        MarkPresented(w, info with { Bounds = target });
    }

    private static void MarkPresented(TrackedWindow w, WindowInfo info)
    {
        w.Presented = true;
        w.Info = info with { IsMinimized = false };
    }

    // ---------------------------------------------------------------- stage membership

    private void Attach(Stage stage, WindowId id)
    {
        stage.Windows.Add(id);
        if (_windows.TryGetValue(id, out var w)) stage.ProcessIds.Add(w.Info.ProcessId);
        stage.Primary ??= id;
    }

    /// <summary>Removes a window from a stage; a stage left empty disappears, and the active stage becomes none.</summary>
    private void Detach(Stage stage, WindowId id)
    {
        stage.Windows.Remove(id);
        if (stage.Primary == id) stage.Primary = stage.Windows.Count > 0 ? stage.Windows[0] : null;
        stage.ProcessIds.Clear();
        foreach (var remaining in stage.Windows)
            if (_windows.TryGetValue(remaining, out var w)) stage.ProcessIds.Add(w.Info.ProcessId);

        if (stage.Windows.Count > 0) return;
        _stages.Remove(stage);
        if (stage == ActiveStage) ActiveStage = null;
    }

    private static WindowId? ResolvePrimary(Stage stage)
    {
        if (stage.Primary is { } p && stage.Windows.Contains(p)) return p;
        return stage.Windows.Count > 0 ? stage.Windows[0] : null;
    }

    private Stage? FindStageByProcess(uint pid)
    {
        if (ActiveStage?.ProcessIds.Contains(pid) == true) return ActiveStage;
        return _stages.FirstOrDefault(s => s.ProcessIds.Contains(pid));
    }

    private Stage CreateStage(WindowInfo info, bool atEnd)
    {
        var stage = new Stage(info.ProcessId, info.ProcessName);
        if (atEnd || _stages.Count == 0) _stages.Add(stage);
        else _stages.Insert(ActiveStage != null && _stages[0] == ActiveStage ? 1 : 0, stage);
        return stage;
    }

    private void MoveToFront(Stage stage)
    {
        _stages.Remove(stage);
        _stages.Insert(0, stage);
    }

    private void Reset()
    {
        _stages.Clear();
        _windows.Clear();
        _pending.Clear();
        _settling.Clear();
        _transitionRestore.Clear();
        _pendingSwap = null;
        _userDragging = null;
        ActiveStage = null;
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void Log(string message) => _log?.Invoke(message);
}
