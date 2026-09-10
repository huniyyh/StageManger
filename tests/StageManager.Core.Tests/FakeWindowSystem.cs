using StageManager.Core;

namespace StageManager.Core.Tests;

/// <summary>In-memory window system: records every command and lets tests mutate window state directly.</summary>
internal sealed class FakeWindowSystem : IWindowSystem
{
    internal sealed class Win
    {
        public required WindowInfo Info { get; set; }
        public bool Minimized { get; set; }
        public RectPx Bounds { get; set; }

        /// <summary>Bounds the window takes when it comes out of the minimized state, like a real window's restore rectangle.</summary>
        public RectPx? BoundsWhenRestored { get; set; }

        public void Restore()
        {
            if (!Minimized) return; // activating a visible window does not move it
            Minimized = false;
            if (BoundsWhenRestored is { } b) Bounds = b;
        }
    }

    private readonly List<WindowId> _order = new();
    public Dictionary<WindowId, Win> Windows { get; } = new();
    public WindowId? Foreground { get; set; }
    public RectPx WorkArea { get; set; } = new(0, 0, 1920, 1040);
    public List<string> Ops { get; } = new();

#pragma warning disable CS0067 // raised by tests through StageEngine.OnWindowEvent directly
    public event Action<WindowEvent>? WindowChanged;
#pragma warning restore CS0067

    public WindowId Add(string title, uint pid, RectPx? bounds = null, bool minimized = false)
    {
        var id = new WindowId(Windows.Count + 1);
        var b = bounds ?? new RectPx(100, 100, 900, 700);
        Windows[id] = new Win
        {
            Info = new WindowInfo(id, title, "cls", pid, "proc" + pid, null, b, minimized, false),
            Minimized = minimized,
            Bounds = b,
        };
        _order.Add(id);
        return id;
    }

    public void Remove(WindowId id)
    {
        Windows.Remove(id);
        _order.Remove(id);
    }

    public bool IsMinimized(WindowId id) => Windows[id].Minimized;
    public RectPx BoundsOf(WindowId id) => Windows[id].Bounds;

    public IReadOnlyList<WindowInfo> EnumerateManageableWindows() => _order.Select(id => GetWindowInfo(id)!).ToList();

    public WindowInfo? GetWindowInfo(WindowId id)
        => Windows.TryGetValue(id, out var w) ? w.Info with { IsMinimized = w.Minimized, Bounds = w.Bounds } : null;

    public WindowId? GetForegroundWindow() => Foreground;
    public RectPx GetPrimaryWorkArea() => WorkArea;

    public RectPx? GetRestoredBounds(WindowId id)
        => Windows.TryGetValue(id, out var w) ? (w.Minimized ? w.BoundsWhenRestored ?? w.Bounds : w.Bounds) : null;

    public void SetTransitionsEnabled(WindowId id, bool enabled) => Ops.Add($"transitions {id.Value} {(enabled ? "on" : "off")}");

    public void Minimize(WindowId id) { Windows[id].Minimized = true; Ops.Add($"min {id.Value}"); }
    public void RestoreNoActivate(WindowId id) { Windows[id].Restore(); Ops.Add($"restore {id.Value}"); }
    public void SetBounds(WindowId id, RectPx b) { Windows[id].Bounds = b; Ops.Add($"move {id.Value} {b}"); }

    public bool Activate(WindowId id)
    {
        Windows[id].Restore();
        Foreground = id;
        Ops.Add($"activate {id.Value}");
        return true;
    }

    public int CaptureCount { get; private set; }

    public Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight, bool fromScreen = false)
    {
        if (Windows[id].Minimized) return null;
        CaptureCount++;
        return new Snapshot(1, 1, new byte[4], DateTimeOffset.UnixEpoch);
    }
}

internal sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}
