using StageManager.Core;

namespace StageManager.Core.Tests;

public class StageEngineTests
{
    private static (FakeWindowSystem ws, StageEngine engine, FakeClock clock) Create()
    {
        var ws = new FakeWindowSystem();
        var clock = new FakeClock();
        var engine = new StageEngine(ws, clock: () => clock.Now);
        return (ws, engine, clock);
    }

    /// <summary>Two windows of app 1 (a1 is foreground) and one window of app 2.</summary>
    private static (FakeWindowSystem ws, StageEngine engine, FakeClock clock, WindowId a1, WindowId a2, WindowId b) CreateEnabled()
    {
        var (ws, engine, clock) = Create();
        var a1 = ws.Add("A1", 1);
        var a2 = ws.Add("A2", 1);
        var b = ws.Add("B", 2);
        ws.Foreground = a1;
        engine.Enable();
        return (ws, engine, clock, a1, a2, b);
    }

    [Fact]
    public void Enable_GroupsWindowsByProcess_AndParksEveryOtherStage()
    {
        var (ws, engine, _, a1, a2, b) = CreateEnabled();

        Assert.True(engine.IsEnabled);
        Assert.Equal(2, engine.Stages.Count);
        Assert.Same(engine.Stages[0], engine.ActiveStage);
        Assert.Equal(new[] { a1, a2 }, engine.ActiveStage!.Windows);
        Assert.Equal(a1, engine.ActiveStage.Primary);

        Assert.False(ws.IsMinimized(a1));
        Assert.False(ws.IsMinimized(a2));
        Assert.True(ws.IsMinimized(b));
        Assert.True(engine.GetWindow(b)!.ParkedByUs);
        Assert.NotNull(engine.GetWindow(b)!.Snapshot);
        Assert.Single(engine.StripStages);
        Assert.Equal(new[] { b }, engine.ParkedByUs);
    }

    [Fact]
    public void Enable_CentersSingleActiveWindowInsideAvailableArea()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1, new RectPx(0, 0, 800, 600));
        ws.Foreground = a;

        engine.Enable();

        // strip on the right by default: available area is (12,12)-(1708,1028): 1696 x 1016
        Assert.Equal(RectPx.FromSize(12 + (1696 - 800) / 2, 12 + (1016 - 600) / 2, 800, 600), ws.BoundsOf(a));
        Assert.True(engine.GetWindow(a)!.MovedByUs);
    }

    [Fact]
    public void Enable_WithNoForegroundWindow_PicksFirstNonMinimizedStage()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1, minimized: true);
        var b = ws.Add("B", 2);
        ws.Foreground = null;

        engine.Enable();

        Assert.Equal(b, engine.ActiveStage!.Primary);
        Assert.False(ws.IsMinimized(b));
        Assert.True(ws.IsMinimized(a));
        Assert.False(engine.GetWindow(a)!.ParkedByUs); // it was already minimized; not ours to restore
    }

    [Fact]
    public void ActivateStage_ParksTheOldStage_AndPresentsTheNewOne()
    {
        var (ws, engine, _, a1, a2, b) = CreateEnabled();
        var stageB = engine.Stages[1];

        engine.ActivateStage(stageB);

        Assert.Same(stageB, engine.ActiveStage);
        Assert.Same(stageB, engine.Stages[0]);
        Assert.False(ws.IsMinimized(b));
        Assert.Equal(b, ws.Foreground);
        Assert.True(ws.IsMinimized(a1));
        Assert.True(ws.IsMinimized(a2));
        Assert.NotNull(engine.GetWindow(a1)!.Snapshot);
        Assert.Contains("activate 3", ws.Ops);
    }

    [Fact]
    public void ActivateStage_RemembersWhereWindowsWereLeft()
    {
        var (ws, engine, _, a1, _, b) = CreateEnabled();
        var stageA = engine.ActiveStage!;
        var stageB = engine.Stages[1];
        var moved = new RectPx(600, 300, 1400, 900);
        ws.Windows[a1].Bounds = moved;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MoveSizeEnded, a1));

        engine.ActivateStage(stageB);
        ws.Windows[a1].Bounds = new RectPx(100, 100, 900, 700); // the shell restored it somewhere else
        engine.ActivateStage(stageA);

        Assert.Equal(moved, ws.BoundsOf(a1));
    }

    [Fact]
    public void ActivateStage_OnActiveStage_JustFocusesItsPrimaryWindow()
    {
        var (ws, engine, _, a1, _, _) = CreateEnabled();
        ws.Ops.Clear();

        engine.ActivateStage(engine.ActiveStage!);

        Assert.Equal(new[] { $"activate {a1.Value}" }, ws.Ops);
    }

    [Fact]
    public void Disable_RestoresParkedWindows_AndOriginalBounds()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1, new RectPx(0, 0, 800, 600));
        var b = ws.Add("B", 2, new RectPx(50, 50, 850, 650));
        ws.Foreground = a;
        engine.Enable();
        Assert.True(ws.IsMinimized(b));

        engine.Disable();

        Assert.False(engine.IsEnabled);
        Assert.Empty(engine.Stages);
        Assert.False(ws.IsMinimized(b));
        Assert.Equal(new RectPx(0, 0, 800, 600), ws.BoundsOf(a));
        Assert.Equal(new RectPx(50, 50, 850, 650), ws.BoundsOf(b));
    }

    [Fact]
    public void WindowMinimizedBeforeEnable_IsRestoredToRealBoundsOnDisable()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1);
        // Minimized windows report Windows' off-screen placeholder rectangle.
        var b = ws.Add("B", 2, new RectPx(-32000, -32000, -31840, -31972), minimized: true);
        ws.Windows[b].BoundsWhenRestored = new RectPx(100, 100, 900, 700);
        ws.Foreground = a;
        engine.Enable();

        engine.ActivateStage(engine.Stages.First(s => s.ProcessId == 2));
        Assert.False(ws.IsMinimized(b));
        Assert.Equal(new RectPx(100, 100, 900, 700), engine.GetWindow(b)!.OriginalBounds);

        engine.Disable();

        Assert.Equal(new RectPx(100, 100, 900, 700), ws.BoundsOf(b));
        Assert.DoesNotContain(ws.Ops, op => op.Contains("-32000"));
    }

    [Fact]
    public void Disable_LeavesUserMinimizedWindowsAlone()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1);
        var b = ws.Add("B", 2, minimized: true);
        ws.Foreground = a;
        engine.Enable();

        engine.Disable();

        Assert.True(ws.IsMinimized(b));
    }

    [Fact]
    public void UserForegroundSwitch_ActivatesThatStage()
    {
        var (ws, engine, _, a1, a2, b) = CreateEnabled();

        // Alt-Tab: the shell restores B and makes it foreground before we hear about it.
        ws.Windows[b].Minimized = false;
        ws.Foreground = b;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Foreground, b));

        Assert.Equal(b, engine.ActiveStage!.Primary);
        Assert.True(ws.IsMinimized(a1));
        Assert.True(ws.IsMinimized(a2));
        Assert.False(engine.GetWindow(b)!.ParkedByUs);
    }

    [Fact]
    public void UserRestoreFromTaskbar_ActivatesThatStage()
    {
        var (ws, engine, _, a1, _, b) = CreateEnabled();

        ws.Windows[b].Minimized = false;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MinimizeEnded, b));

        Assert.Equal(b, engine.ActiveStage!.Primary);
        Assert.True(ws.IsMinimized(a1));
    }

    [Fact]
    public void ForegroundWithinActiveStage_OnlyUpdatesPrimary()
    {
        var (ws, engine, _, _, a2, b) = CreateEnabled();
        ws.Ops.Clear();

        ws.Foreground = a2;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Foreground, a2));

        Assert.Equal(a2, engine.ActiveStage!.Primary);
        Assert.Empty(ws.Ops);
        Assert.True(ws.IsMinimized(b));
    }

    [Fact]
    public void NewBackgroundWindow_IsParkedAfterGracePeriod()
    {
        var (ws, engine, clock, _, _, _) = CreateEnabled();
        var stageA = engine.ActiveStage!;
        var c = ws.Add("C", 3);

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, c));
        Assert.Equal(3, engine.Stages.Count);
        Assert.False(ws.IsMinimized(c));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        engine.Tick();
        Assert.False(ws.IsMinimized(c));

        clock.Advance(TimeSpan.FromMilliseconds(400));
        engine.Tick();
        Assert.True(ws.IsMinimized(c));
        Assert.True(engine.GetWindow(c)!.ParkedByUs);
        Assert.Same(stageA, engine.ActiveStage);
    }

    [Fact]
    public void NewWindow_ThatBecomesForeground_ActivatesItsStage()
    {
        var (ws, engine, clock, a1, _, _) = CreateEnabled();
        var c = ws.Add("C", 3);

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, c));
        ws.Foreground = c;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Foreground, c));

        Assert.Equal(c, engine.ActiveStage!.Primary);
        Assert.True(ws.IsMinimized(a1));
        Assert.False(ws.IsMinimized(c));

        clock.Advance(TimeSpan.FromSeconds(1));
        engine.Tick();
        Assert.False(ws.IsMinimized(c));
    }

    [Fact]
    public void NewWindow_AlreadyForegroundWhenShown_ActivatesImmediately()
    {
        var (ws, engine, _, a1, _, _) = CreateEnabled();
        var c = ws.Add("C", 3);
        ws.Foreground = c;

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, c));

        Assert.Equal(c, engine.ActiveStage!.Primary);
        Assert.True(ws.IsMinimized(a1));
    }

    [Fact]
    public void NewWindowOfActiveApp_JoinsActiveStage_AndStaysVisible()
    {
        var (ws, engine, clock, a1, a2, _) = CreateEnabled();
        var a3 = ws.Add("A3", 1, new RectPx(1400, 0, 2200, 600)); // overlaps the strip on the right

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, a3));

        Assert.Equal(new[] { a1, a2, a3 }, engine.ActiveStage!.Windows);
        Assert.False(ws.IsMinimized(a3));
        Assert.Equal(RectPx.FromSize(1708 - 800, 12, 800, 600), ws.BoundsOf(a3)); // nudged out of the strip, not centered

        clock.Advance(TimeSpan.FromSeconds(1));
        engine.Tick();
        Assert.False(ws.IsMinimized(a3));
    }

    [Fact]
    public void DestroyedWindow_IsRemoved_AndEmptyStageDisappears()
    {
        var (ws, engine, _, _, _, b) = CreateEnabled();

        ws.Remove(b);
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Destroyed, b));

        Assert.Single(engine.Stages);
        Assert.Null(engine.GetWindow(b));
    }

    [Fact]
    public void DestroyingLastWindowOfActiveStage_LeavesNoActiveStage()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1);
        var b = ws.Add("B", 2);
        ws.Foreground = a;
        engine.Enable();

        ws.Remove(a);
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Destroyed, a));

        Assert.Null(engine.ActiveStage);
        Assert.Single(engine.Stages);
        Assert.True(ws.IsMinimized(b));
    }

    [Fact]
    public void UserMinimize_OfSingleWindowStage_SendsStageToStrip()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1);
        var b = ws.Add("B", 2);
        ws.Foreground = a;
        engine.Enable();
        var stageA = engine.ActiveStage!;

        ws.Windows[a].Minimized = true;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MinimizeStarted, a));

        Assert.Null(engine.ActiveStage);
        Assert.Equal(2, engine.Stages.Count);
        Assert.Contains(stageA, engine.StripStages);
        Assert.False(engine.GetWindow(a)!.ParkedByUs);
        Assert.True(ws.IsMinimized(b));
    }

    [Fact]
    public void UserMinimize_InMultiWindowStage_DetachesWindowIntoItsOwnStage()
    {
        var (ws, engine, _, a1, a2, _) = CreateEnabled();

        ws.Windows[a2].Minimized = true;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MinimizeStarted, a2));

        Assert.Equal(new[] { a1 }, engine.ActiveStage!.Windows);
        Assert.Equal(3, engine.Stages.Count);
        Assert.Equal(new[] { a2 }, engine.Stages[1].Windows);
        Assert.Equal(a2, engine.Stages[1].Primary);
    }

    [Fact]
    public void MinimizeEventsCausedByParking_AreIgnored()
    {
        var (ws, engine, _, _, _, b) = CreateEnabled();
        var stageA = engine.ActiveStage!;

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MinimizeStarted, b));

        Assert.Same(stageA, engine.ActiveStage);
        Assert.Equal(2, engine.Stages.Count);
        Assert.True(engine.GetWindow(b)!.ParkedByUs);
        Assert.True(ws.IsMinimized(b));
    }

    [Fact]
    public void RestoreEventsCausedByPresenting_AreIgnored()
    {
        var (ws, engine, _, a1, a2, b) = CreateEnabled();
        var stageB = engine.Stages[1];
        engine.ActivateStage(stageB);
        ws.Ops.Clear();

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MinimizeEnded, b));

        Assert.Same(stageB, engine.ActiveStage);
        Assert.Empty(ws.Ops);
        Assert.True(ws.IsMinimized(a1));
        Assert.True(ws.IsMinimized(a2));
    }

    [Fact]
    public void TitleChange_UpdatesInfo_AndRaisesChanged()
    {
        var (ws, engine, _, a1, _, _) = CreateEnabled();
        int changed = 0;
        engine.Changed += () => changed++;
        ws.Windows[a1].Info = ws.Windows[a1].Info with { Title = "A1 - edited" };

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.TitleChanged, a1));

        Assert.Equal("A1 - edited", engine.GetWindow(a1)!.Info.Title);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void WindowThatBecomesUnmanageable_IsRemoved()
    {
        var (ws, engine, _, _, _, b) = CreateEnabled();

        ws.Remove(b); // GetWindowInfo now returns null
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.TitleChanged, b));

        Assert.Single(engine.Stages);
    }

    // ---------------------------------------------------------------- settling (apps that move themselves)

    private static (FakeWindowSystem ws, StageEngine engine, FakeClock clock, WindowId c) CreateWithFreshForegroundWindow()
    {
        var (ws, engine, clock, _, _, _) = CreateEnabled();
        var c = ws.Add("C", 3, new RectPx(0, 0, 800, 600));
        ws.Foreground = c;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, c));
        Assert.Equal(RectPx.FromSize(12 + (1696 - 800) / 2, 12 + (1016 - 600) / 2, 800, 600), ws.BoundsOf(c));
        return (ws, engine, clock, c);
    }

    [Fact]
    public void NewWindow_ThatRepositionsItself_IsPlacedAgainAfterSettling()
    {
        var (ws, engine, clock, c) = CreateWithFreshForegroundWindow();

        // Like a browser restoring its saved placement: it moves and resizes on its own, without a user drag.
        ws.Windows[c].Bounds = new RectPx(40, 30, 1040, 730);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        engine.Tick();

        // Centered again, but at the size the app chose.
        Assert.Equal(RectPx.FromSize(12 + (1696 - 1000) / 2, 12 + (1016 - 700) / 2, 1000, 700), ws.BoundsOf(c));
    }

    [Fact]
    public void NewWindow_ThatStaysPut_IsNotTouchedAgain()
    {
        var (ws, engine, clock, _) = CreateWithFreshForegroundWindow();
        ws.Ops.Clear();

        clock.Advance(TimeSpan.FromMilliseconds(600));
        engine.Tick();

        Assert.Empty(ws.Ops);
    }

    [Fact]
    public void UserDragBeforeSettling_IsRespected()
    {
        var (ws, engine, clock, c) = CreateWithFreshForegroundWindow();
        var dragged = new RectPx(300, 200, 1100, 800);
        ws.Windows[c].Bounds = dragged;
        engine.OnWindowEvent(new WindowEvent(WindowEventKind.MoveSizeEnded, c));
        ws.Ops.Clear();

        clock.Advance(TimeSpan.FromMilliseconds(600));
        engine.Tick();

        Assert.Empty(ws.Ops);
        Assert.Equal(dragged, ws.BoundsOf(c));
    }

    [Fact]
    public void Settling_GivesUpAfterTwoAttempts()
    {
        var (ws, engine, clock, c) = CreateWithFreshForegroundWindow();

        for (int i = 0; i < 3; i++)
        {
            ws.Windows[c].Bounds = new RectPx(0, 0, 800, 600); // the app insists
            clock.Advance(TimeSpan.FromMilliseconds(600));
            engine.Tick();
        }

        // one initial placement + two corrections, then we leave it alone
        Assert.Equal(3, ws.Ops.Count(op => op.StartsWith($"move {c.Value} ")));
        Assert.Equal(new RectPx(0, 0, 800, 600), ws.BoundsOf(c));
    }

    [Fact]
    public void Settling_IsCancelledWhenWindowIsParked()
    {
        var (ws, engine, clock, c) = CreateWithFreshForegroundWindow();
        var stageA = engine.Stages.First(s => s.ProcessId == 1);
        engine.ActivateStage(stageA); // parks C
        ws.Windows[c].Bounds = new RectPx(0, 0, 800, 600);
        ws.Ops.Clear();

        clock.Advance(TimeSpan.FromMilliseconds(600));
        engine.Tick();

        Assert.DoesNotContain(ws.Ops, op => op.StartsWith($"move {c.Value} "));
    }

    [Fact]
    public void EventsWhileDisabled_AreIgnored()
    {
        var (ws, engine, _) = Create();
        var a = ws.Add("A", 1);

        engine.OnWindowEvent(new WindowEvent(WindowEventKind.Shown, a));
        engine.Tick();

        Assert.Empty(engine.Stages);
        Assert.Empty(ws.Ops);
    }
}
