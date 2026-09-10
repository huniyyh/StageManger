using System.Windows;
using System.Windows.Threading;
using StageManager.Core;
using StageManager.Win32;

namespace StageManager.App;

public partial class App : System.Windows.Application
{
    private Win32WindowSystem? _ws;
    private StageEngine? _engine;
    private StripWindow? _strip;
    private TrayIcon? _tray;
    private HotkeyHost? _hotkey;
    private DispatcherTimer? _tick;
    private ParkedWindowStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("FATAL " + args.Exception);
            SafeDisable();
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("FATAL " + args.ExceptionObject);
            SafeDisable();
        };

        _ws = new Win32WindowSystem(Log.Write);
        _engine = new StageEngine(_ws, log: Log.Write);
        _store = new ParkedWindowStore();
        _store.RestoreAfterCrash(_ws);

        _ws.WindowChanged += ev => _engine.OnWindowEvent(ev);
        _ws.StartListening();

        _strip = new StripWindow(_engine, _ws);
        _engine.Changed += OnEngineChanged;

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => _engine.Tick(), Dispatcher);
        _tick.Start();

        _hotkey = new HotkeyHost();
        _hotkey.Pressed += Toggle;
        _tray = new TrayIcon(Toggle, Shutdown);

        Log.Write("started");
        if (e.Args.Contains("--enable")) Toggle();

        // "--exit-after N" quits after N seconds; used by smoke tests.
        int exitIndex = Array.IndexOf(e.Args, "--exit-after");
        if (exitIndex >= 0 && exitIndex + 1 < e.Args.Length && int.TryParse(e.Args[exitIndex + 1], out int seconds))
        {
            var quit = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) => Shutdown(), Dispatcher);
            quit.Start();
        }
    }

    private void OnEngineChanged()
    {
        _strip?.Refresh();
        if (_engine!.IsEnabled) _store?.Save(_engine.ParkedByUs);
        else _store?.Clear();
    }

    private void Toggle()
    {
        if (_engine == null) return;
        if (_engine.IsEnabled) _engine.Disable();
        else _engine.Enable();
        _tray?.Update(_engine.IsEnabled);
    }

    private void SafeDisable()
    {
        try
        {
            _engine?.Disable();
            _store?.Clear();
        }
        catch (Exception ex)
        {
            Log.Write("disable failed: " + ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SafeDisable();
        _tick?.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _ws?.Dispose();
        Log.Write("exited");
        base.OnExit(e);
    }
}
