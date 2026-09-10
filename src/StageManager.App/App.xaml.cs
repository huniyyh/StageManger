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
    private DispatcherTimer? _updateTimer;
    private ParkedWindowStore? _store;
    private Updater? _updater;
    private Mutex? _singleInstance;

    /// <summary>Per-session lock so only one Stage Manager runs; a second one would fight the first over the same windows.</summary>
    private const string SingleInstanceMutexName = @"Local\StageManager.SingleInstance";

    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init();

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Hand the request over to the running instance and leave quietly.
            nint running = NativeWindow.FindMessageOnlyWindow(HotkeyHost.WindowTitle);
            if (running != 0) NativeWindow.PostMessage(running, HotkeyHost.WM_ANOTHER_INSTANCE, e.Args.Contains("--enable") ? 1 : 0);
            Log.Write("another instance is already running; exiting");
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

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
        _hotkey.AnotherInstanceStarted += OnAnotherInstanceStarted;
        _tray = new TrayIcon(Toggle, () => _ = CheckForUpdatesAsync(interactive: true), Shutdown);

        _updater = new Updater();
        _updater.UpdateReady += version => _tray?.ShowUpdateReady(version, ApplyUpdate);
        _updateTimer = new DispatcherTimer(FirstUpdateCheck, DispatcherPriority.Background, OnUpdateTimer, Dispatcher);
        _updateTimer.Start();

        Log.Write($"started v{_updater.CurrentVersion}{(_updater.IsInstalled ? "" : " (not installed)")}");
        if (e.Args.Contains("--enable")) Toggle();
        if (e.Args.Contains("--check-updates")) _ = CheckForUpdatesAsync(interactive: false);

        // "--startup on | on-enabled | off" sets the run-at-logon choice from the command line (scripts, tests).
        int startupIndex = Array.IndexOf(e.Args, "--startup");
        if (startupIndex >= 0 && startupIndex + 1 < e.Args.Length)
        {
            var choice = e.Args[startupIndex + 1].ToLowerInvariant();
            StartupRegistration.Write(runAtLogon: choice != "off", startEnabled: choice == "on-enabled");
        }

        if (Program.IsFirstRun)
            _tray.ShowBalloon("Stage Manager 설치됨", "트레이 아이콘이나 Ctrl+Alt+S 로 켜세요. 트레이 메뉴에서 Windows 시작 시 실행을 켤 수 있습니다.");

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

    private void OnAnotherInstanceStarted(bool wantsEnable)
    {
        Log.Write($"another instance was launched (enable requested: {wantsEnable})");
        if (wantsEnable && _engine is { IsEnabled: false }) Toggle();
        _tray?.ShowBalloon("Stage Manager", _engine?.IsEnabled == true
            ? "이미 실행 중입니다. 켜져 있어요."
            : "이미 실행 중입니다. 트레이 아이콘이나 Ctrl+Alt+S 로 켜세요.");
    }

    // ---------------------------------------------------------------- updates

    private void OnUpdateTimer(object? sender, EventArgs e)
    {
        _updateTimer!.Interval = UpdateCheckInterval; // the first check comes soon after start, later ones every few hours
        _ = CheckForUpdatesAsync(interactive: false);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updater == null) return;
        var result = await _updater.CheckAsync();
        if (!interactive) return;
        switch (result)
        {
            case UpdateCheckResult.UpToDate:
                _tray?.ShowBalloon("Stage Manager", $"최신 버전입니다 (v{_updater.CurrentVersion}).");
                break;
            case UpdateCheckResult.NotInstalled:
                _tray?.ShowBalloon("Stage Manager", "설치된 빌드가 아니라서 업데이트 확인을 건너뜁니다.");
                break;
            case UpdateCheckResult.Failed:
                _tray?.ShowBalloon("업데이트 확인 실패", _updater.LastError ?? "원인을 알 수 없습니다. 로그를 확인하세요.");
                break;
        }
    }

    /// <summary>Puts the user's windows back, drops the tray icon and lets Velopack swap in the new version and restart.</summary>
    private void ApplyUpdate()
    {
        if (_updater == null) return;
        SafeDisable();
        _tray?.Dispose();
        _tray = null;
        _updater.ApplyAndRestart(); // exits this process
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
        _updateTimer?.Stop();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _ws?.Dispose();
        if (_singleInstance != null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
            Log.Write("exited");
        }
        base.OnExit(e);
    }
}
