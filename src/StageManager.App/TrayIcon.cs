using StageManager.Win32;

namespace StageManager.App;

/// <summary>Notification-area icon with the toggle, startup, update and exit commands. Built on Shell_NotifyIcon, not WinForms.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IdToggle = 1;
    private const int IdRunAtLogon = 2;
    private const int IdStartEnabled = 3;
    private const int IdCheckUpdates = 4;
    private const int IdApplyUpdate = 5;
    private const int IdQuit = 6;

    private readonly NotificationIcon _icon;
    private readonly nint _iconHandle;
    private readonly Action _toggle;
    private readonly Action _checkUpdates;
    private readonly Action _exit;
    private Action? _applyUpdate;
    private string? _readyVersion;
    private bool _enabled;
    private DateTime _lastClickToggle = DateTime.MinValue;

    public TrayIcon(AppMessageWindow window, Action toggle, Action checkUpdates, Action exit)
    {
        _toggle = toggle;
        _checkUpdates = checkUpdates;
        _exit = exit;
        _iconHandle = NotificationIcon.ExtractFileIcon(Environment.ProcessPath ?? "");
        _icon = new NotificationIcon(window.Handle, _iconHandle != 0 ? _iconHandle : NotificationIcon.DefaultApplicationIcon(), "Stage Manager (꺼짐)");
        window.TrayInteraction += OnInteraction;
        window.TaskbarCreated += () => _icon.Add(); // Explorer restarted; the icon has to be registered again
    }

    private void OnInteraction(TrayEvent interaction)
    {
        switch (interaction)
        {
            case TrayEvent.LeftClick:
            case TrayEvent.LeftDoubleClick:
                ToggleOnce();
                break;
            case TrayEvent.ContextMenu:
                ShowMenu();
                break;
            case TrayEvent.BalloonClicked:
                _applyUpdate?.Invoke();
                break;
        }
    }

    private void ShowMenu()
    {
        var (runAtLogon, startEnabled) = StartupRegistration.Read();
        var entries = new List<TrayMenuEntry>
        {
            new(IdToggle, _enabled ? "Stage Manager 끄기\tCtrl+Alt+S" : "Stage Manager 켜기\tCtrl+Alt+S"),
            TrayMenuEntry.Separator,
            new(IdRunAtLogon, "Windows 시작 시 실행", Checked: runAtLogon),
            new(IdStartEnabled, "시작 시 바로 켜기", Checked: startEnabled, Enabled: runAtLogon),
            TrayMenuEntry.Separator,
            new(IdCheckUpdates, "업데이트 확인"),
        };
        if (_readyVersion != null) entries.Add(new TrayMenuEntry(IdApplyUpdate, $"v{_readyVersion} 로 업데이트 (재시작)"));
        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new TrayMenuEntry(IdQuit, "종료"));

        switch (_icon.ShowMenu(entries))
        {
            case IdToggle: _toggle(); break;
            case IdRunAtLogon: StartupRegistration.Write(!runAtLogon, !runAtLogon && startEnabled); break;
            case IdStartEnabled: StartupRegistration.Write(true, !startEnabled); break;
            case IdCheckUpdates: _checkUpdates(); break;
            case IdApplyUpdate: _applyUpdate?.Invoke(); break;
            case IdQuit: _exit(); break;
        }
    }

    public void Update(bool enabled)
    {
        _enabled = enabled;
        _icon.SetTip(enabled ? "Stage Manager (켜짐)" : "Stage Manager (꺼짐)");
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloon(title, text, 3000);

    /// <summary>Offers a downloaded update: a menu entry appears and a balloon that applies it when clicked.</summary>
    public void ShowUpdateReady(string version, Action apply)
    {
        _readyVersion = version;
        _applyUpdate = apply;
        _icon.ShowBalloon("Stage Manager 업데이트", $"v{version} 이 준비됐습니다. 클릭하면 재시작해서 적용합니다.", 8000);
    }

    /// <summary>A double-click, or any burst of clicks inside the system double-click interval, toggles exactly once.</summary>
    private void ToggleOnce()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastClickToggle).TotalMilliseconds < NotificationIcon.DoubleClickTime) return;
        _lastClickToggle = now;
        _toggle();
    }

    public void Dispose()
    {
        _icon.Dispose();
        NotificationIcon.DestroyIcon(_iconHandle);
    }
}
