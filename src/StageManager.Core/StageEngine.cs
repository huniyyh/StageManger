namespace StageManager.Core;

/// <summary>
/// The Stage Manager state machine. Exactly one stage is active and laid out in the center of the screen;
/// every other stage is parked (its windows minimized) and represented by a thumbnail in the strip.
/// All members must be called from a single thread.
/// </summary>
public sealed class StageEngine
{
    public const int SnapshotMaxWidth = 480;
    public const int SnapshotMaxHeight = 320;

    /// <summary>How long a newly shown background window may wait to become foreground before it is parked.</summary>
    public static readonly TimeSpan PendingDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How long after a window is first placed on stage we check whether its app moved it on its own.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How many times a self-moving window is put back before we give up.</summary>
    public const int MaxSettleAttempts = 2;

    private readonly IWindowSystem _ws;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly List<Stage> _stages = new();
    private readonly Dictionary<WindowId, TrackedWindow> _windows = new();
    private readonly Dictionary<WindowId, DateTimeOffset> _pending = new();
    private readonly Dictionary<WindowId, (DateTimeOffset Due, int Attempts)> _settling = new();

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
            var stage = FindStageByProcess(info.ProcessId) ?? CreateStage(info, atEnd: true);
            stage.Windows.Add(info.Id);
            stage.Primary ??= info.Id;
        }

        Stage? active = fg is { } f ? FindStageOf(f) : null;
        active ??= _stages.FirstOrDefault(s => s.Windows.Any(w => !_windows[w].Info.IsMinimized));

        foreach (var s in _stages)
            if (s != active) Park(s);

        if (active != null)
        {
            if (fg is { } f2 && active.Windows.Contains(f2)) active.Primary = f2;
            ActiveStage = active;
            Present(active, activate: false);
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
        Reset();
        IsEnabled = false;
        Log("disabled");
        RaiseChanged();
    }

    /// <summary>Brings a stage to the center; the previously active stage is parked.</summary>
    public void ActivateStage(Stage stage)
    {
        if (!IsEnabled || !_stages.Contains(stage)) return;
        if (stage == ActiveStage)
        {
            if (stage.Primary is { } p) _ws.Activate(p);
            return;
        }

        Log($"activate {stage}");
        var previous = ActiveStage;
        ActiveStage = stage;
        if (previous != null) Park(previous);
        Present(stage, activate: true);
        MoveToFront(stage);
        RaiseChanged();
    }

    /// <summary>
    /// Call periodically (a few times per second). Parks new windows that never became foreground and
    /// puts back windows whose app moved them right after we placed them.
    /// </summary>
    public void Tick()
    {
        if (!IsEnabled) return;
        if (_pending.Count > 0) ProcessPending();
        if (_settling.Count > 0) ProcessSettling();
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
            case WindowEventKind.Cloaked:
            case WindowEventKind.Destroyed: RemoveWindow(e.Window); break;
            case WindowEventKind.MinimizeStarted: OnMinimizeStarted(e.Window); break;
            case WindowEventKind.MinimizeEnded: OnMinimizeEnded(e.Window); break;
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
        if (info == null)
        {
            if (_windows.ContainsKey(id)) RemoveWindow(id);
            return;
        }

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
        var snap = _ws.CaptureSnapshot(id, SnapshotMaxWidth, SnapshotMaxHeight);
        if (snap != null) w.Snapshot = snap;
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
                stage.Windows.Remove(id);
                if (stage.Primary == id) stage.Primary = stage.Windows[0];
                var own = new Stage(w.Info.ProcessId, w.Info.ProcessName) { Primary = id };
                own.Windows.Add(id);
                _stages.Insert(Math.Min(1, _stages.Count), own);
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
        if (stage == ActiveStage) return; // restored by Present, or already handled by OnForeground

        // The user restored a parked window from the taskbar.
        w.ParkedByUs = false;
        stage.Primary = id;
        ActivateStage(stage);
    }

    private void OnMoveSizeEnded(WindowId id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        var info = _ws.GetWindowInfo(id);
        if (info == null) return;
        w.Info = info;
        _settling.Remove(id); // the user took over; never fight a drag
        if (FindStageOf(id) == ActiveStage) w.StageBounds = info.Bounds;
    }

    // ---------------------------------------------------------------- window bookkeeping

    private void AddWindow(WindowInfo info, bool isForeground)
    {
        var w = new TrackedWindow(info) { OriginalBounds = info.IsMinimized ? null : info.Bounds };
        _windows[info.Id] = w;
        var stage = FindStageByProcess(info.ProcessId) ?? CreateStage(info, atEnd: false);
        stage.Windows.Add(info.Id);
        stage.Primary ??= info.Id;
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
        if (!_windows.Remove(id, out var w)) return;

        var stage = FindStageOf(id);
        if (stage != null)
        {
            stage.Windows.Remove(id);
            if (stage.Primary == id) stage.Primary = stage.Windows.Count > 0 ? stage.Windows[0] : null;
            if (stage.Windows.Count == 0)
            {
                _stages.Remove(stage);
                if (stage == ActiveStage) ActiveStage = null;
            }
        }

        Log($"remove '{w.Info.Title}'");
        RaiseChanged();
    }

    private void Park(Stage stage)
    {
        foreach (var id in stage.Windows.ToArray()) ParkWindow(id);
    }

    private void ParkWindow(WindowId id)
    {
        if (!_windows.TryGetValue(id, out var w)) return;
        var info = _ws.GetWindowInfo(id);
        if (info == null) return;
        w.Info = info;
        _settling.Remove(id);
        if (info.IsMinimized) return;

        w.StageBounds = info.Bounds;
        var snap = _ws.CaptureSnapshot(id, SnapshotMaxWidth, SnapshotMaxHeight);
        if (snap != null) w.Snapshot = snap;
        w.ParkedByUs = true; // set before minimizing so the resulting event is recognised as ours
        _ws.Minimize(id);
        w.Info = info with { IsMinimized = true };
    }

    private void Present(Stage stage, bool activate)
    {
        var available = StageLayout.AvailableArea(_ws.GetPrimaryWorkArea(), Layout);
        bool single = stage.Windows.Count == 1;

        foreach (var id in stage.Windows.ToArray())
        {
            if (!_windows.TryGetValue(id, out var w)) continue;
            var info = _ws.GetWindowInfo(id);
            if (info == null) continue;

            if (info.IsMinimized)
            {
                _ws.RestoreNoActivate(id);
                w.ParkedByUs = false;
                info = _ws.GetWindowInfo(id) ?? (info with { IsMinimized = false });
            }
            w.OriginalBounds ??= info.Bounds; // first time we see it un-minimized
            PlaceWindow(w, info, available, center: single && !w.Presented);
        }

        if (!activate) return;
        var primary = stage.Primary is { } p && stage.Windows.Contains(p) ? p : stage.Windows.FirstOrDefault();
        if (primary == default) return;
        _ws.Activate(primary);
        stage.Primary = primary;
    }

    /// <param name="source">Bounds to fit into the area; defaults to the remembered stage bounds, or the current bounds on first placement.</param>
    private void PlaceWindow(TrackedWindow w, WindowInfo info, RectPx available, bool center, RectPx? source = null)
    {
        if (!info.IsMaximized)
        {
            var from = source ?? (w.Presented && w.StageBounds is { } remembered ? remembered : info.Bounds);
            var target = StageLayout.Place(from, available, center);
            if (target != info.Bounds)
            {
                _ws.SetBounds(info.Id, target);
                w.MovedByUs = true;
            }
            w.StageBounds = target;
            info = info with { Bounds = target };

            // Some apps reposition themselves right after they appear; check back once they have had a moment.
            if (!w.Presented) _settling[info.Id] = (_clock() + SettleDelay, 0);
        }
        w.Presented = true;
        w.Info = info with { IsMinimized = false };
    }

    // ---------------------------------------------------------------- helpers

    private Stage? FindStageByProcess(uint pid)
    {
        if (ActiveStage?.ProcessId == pid) return ActiveStage;
        return _stages.FirstOrDefault(s => s.ProcessId == pid);
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
        ActiveStage = null;
    }

    private void RaiseChanged() => Changed?.Invoke();

    private void Log(string message) => _log?.Invoke(message);
}
