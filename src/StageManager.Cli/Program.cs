using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StageManager.Core;
using StageManager.Win32;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.Length > 0 ? args[0] : "help";
using var ws = new Win32WindowSystem(m => Console.Error.WriteLine("[win32] " + m));

switch (command)
{
    case "list":
        return List(ws);
    case "watch":
        return Watch(ws, args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 10);
    case "snap":
        return Snap(ws, args.Length > 1 ? args[1] : "fg", args.Length > 2 ? args[2] : "snapshot.png");
    case "plan":
        return Plan(ws);
    default:
        Console.WriteLine("""
            stagectl list              manageable windows as the engine sees them
            stagectl watch [seconds]   print window events for a while
            stagectl snap <fg|0xHWND> [file.png]   capture a thumbnail the way the strip does
            stagectl plan              dry run: show what enabling Stage Manager would do, without touching windows
            """);
        return 0;
}

static int List(Win32WindowSystem ws)
{
    Console.WriteLine($"work area: {ws.GetPrimaryWorkArea()}   foreground: {ws.GetForegroundWindow()}");
    foreach (var w in ws.EnumerateManageableWindows())
    {
        var state = w.IsMinimized ? "min" : w.IsMaximized ? "max" : "   ";
        Console.WriteLine($"{w.Id,-12} pid={w.ProcessId,-6} {w.ProcessName,-24} {state} {w.Bounds,-22} [{w.ClassName}] {w.Title}");
    }
    return 0;
}

static int Watch(Win32WindowSystem ws, int seconds)
{
    ws.WindowChanged += e =>
    {
        var info = ws.GetWindowInfo(e.Window);
        var what = info == null ? "(not manageable)" : $"{info.ProcessName} | {info.Title}";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {e.Kind,-15} {e.Window,-12} {what}");
    };
    ws.StartListening();
    Console.WriteLine($"watching window events for {seconds}s ...");
    MessagePump.RunFor(TimeSpan.FromSeconds(seconds));
    return 0;
}

static int Snap(Win32WindowSystem ws, string target, string outputPath)
{
    WindowId id;
    if (target == "fg")
    {
        if (ws.GetForegroundWindow() is not { } fg) { Console.WriteLine("no foreground window"); return 1; }
        id = fg;
    }
    else
    {
        id = new WindowId((nint)Convert.ToInt64(target.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16));
    }

    var info = ws.GetWindowInfo(id);
    Console.WriteLine($"target: {id} {info?.ProcessName} | {info?.Title}");
    var snapshot = ws.CaptureSnapshot(id, StageEngine.SnapshotMaxWidth, StageEngine.SnapshotMaxHeight);
    if (snapshot == null) { Console.WriteLine("capture failed"); return 1; }

    var bitmap = BitmapSource.Create(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgr32, null, snapshot.Bgra, snapshot.Stride);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var file = File.Create(outputPath)) encoder.Save(file);
    Console.WriteLine($"saved {snapshot.Width}x{snapshot.Height} -> {Path.GetFullPath(outputPath)}");
    return 0;
}

static int Plan(Win32WindowSystem ws)
{
    var dry = new DryRunWindowSystem(ws);
    var engine = new StageEngine(dry, log: m => Console.WriteLine("[engine] " + m));
    engine.Enable();

    Console.WriteLine();
    Console.WriteLine("stages (most recent first, * = active):");
    foreach (var stage in engine.Stages)
    {
        var titles = stage.Windows.Select(id => engine.GetWindow(id)?.Info.Title ?? "?");
        Console.WriteLine($"  {(stage == engine.ActiveStage ? "*" : " ")} {stage.Label,-22} {string.Join("  |  ", titles)}");
    }

    Console.WriteLine();
    Console.WriteLine("commands that would have been issued:");
    foreach (var op in dry.Ops) Console.WriteLine("  " + op);
    return 0;
}

/// <summary>Reads through to the real window system but only records the commands.</summary>
sealed class DryRunWindowSystem : IWindowSystem
{
    private readonly Win32WindowSystem _real;
    private readonly HashSet<WindowId> _pretendMinimized = new();
    private readonly Dictionary<WindowId, RectPx> _pretendBounds = new();

    public List<string> Ops { get; } = new();

    public DryRunWindowSystem(Win32WindowSystem real) => _real = real;

#pragma warning disable CS0067
    public event Action<WindowEvent>? WindowChanged;
#pragma warning restore CS0067

    public IReadOnlyList<WindowInfo> EnumerateManageableWindows() => _real.EnumerateManageableWindows();

    public WindowInfo? GetWindowInfo(WindowId id)
    {
        var info = _real.GetWindowInfo(id);
        if (info == null) return null;
        if (_pretendMinimized.Contains(id)) info = info with { IsMinimized = true };
        if (_pretendBounds.TryGetValue(id, out var b)) info = info with { Bounds = b };
        return info;
    }

    public WindowId? GetForegroundWindow() => _real.GetForegroundWindow();
    public RectPx GetPrimaryWorkArea() => _real.GetPrimaryWorkArea();
    public RectPx? GetRestoredBounds(WindowId id) => _real.GetRestoredBounds(id);
    public PointPx GetCursorPosition() => _real.GetCursorPosition();
    public void SetTransitionsEnabled(WindowId id, bool enabled) => Ops.Add($"transitions {Describe(id)} {(enabled ? "on" : "off")}");
    public void Minimize(WindowId id) { _pretendMinimized.Add(id); Ops.Add($"minimize {Describe(id)}"); }
    public void RestoreNoActivate(WindowId id) { _pretendMinimized.Remove(id); Ops.Add($"restore  {Describe(id)}"); }
    public void SetBounds(WindowId id, RectPx bounds) { _pretendBounds[id] = bounds; Ops.Add($"move     {Describe(id)} -> {bounds}"); }
    public bool Activate(WindowId id) { Ops.Add($"activate {Describe(id)}"); return true; }

    public Snapshot? CaptureSnapshot(WindowId id, int maxWidth, int maxHeight, bool fromScreen = false)
    {
        var snap = _real.CaptureSnapshot(id, maxWidth, maxHeight, fromScreen);
        Ops.Add($"snapshot {Describe(id)} -> {(snap == null ? "failed" : $"{snap.Width}x{snap.Height}")}");
        return snap;
    }

    private string Describe(WindowId id) => $"{id} '{_real.GetWindowInfo(id)?.Title}'";
}
