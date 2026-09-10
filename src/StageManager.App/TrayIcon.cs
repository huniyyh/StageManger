using System.Windows.Forms;

namespace StageManager.App;

/// <summary>Notification-area icon with the toggle, update and exit commands.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string EnableText = "Stage Manager 켜기  (Ctrl+Alt+S)";
    private const string DisableText = "Stage Manager 끄기  (Ctrl+Alt+S)";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _applyUpdate;
    private readonly Action _toggleAction;
    private Action? _applyUpdateAction;
    private DateTime _lastClickToggle = DateTime.MinValue;

    public TrayIcon(Action toggle, Action checkUpdates, Action exit)
    {
        _toggleAction = toggle;
        var menu = new ContextMenuStrip();
        _toggle = new ToolStripMenuItem(EnableText);
        _toggle.Click += (_, _) => toggle();
        var check = new ToolStripMenuItem("업데이트 확인");
        check.Click += (_, _) => checkUpdates();
        _applyUpdate = new ToolStripMenuItem("업데이트 적용 (재시작)") { Visible = false };
        _applyUpdate.Click += (_, _) => _applyUpdateAction?.Invoke();
        var quit = new ToolStripMenuItem("종료");
        quit.Click += (_, _) => exit();
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(check);
        menu.Items.Add(_applyUpdate);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Stage Manager (꺼짐)",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleOnce(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleOnce(); };
        _icon.BalloonTipClicked += (_, _) => _applyUpdateAction?.Invoke();
    }

    public void Update(bool enabled)
    {
        _toggle.Text = enabled ? DisableText : EnableText;
        _icon.Text = enabled ? "Stage Manager (켜짐)" : "Stage Manager (꺼짐)";
    }

    public void ShowBalloon(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    /// <summary>Offers a downloaded update: a menu item appears and a balloon that applies it when clicked.</summary>
    public void ShowUpdateReady(string version, Action apply)
    {
        _applyUpdateAction = apply;
        _applyUpdate.Text = $"v{version} 로 업데이트 (재시작)";
        _applyUpdate.Visible = true;
        _icon.ShowBalloonTip(8000, "Stage Manager 업데이트", $"v{version} 이 준비됐습니다. 클릭하면 재시작해서 적용합니다.", ToolTipIcon.Info);
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } own) return own;
        }
        catch (Exception ex)
        {
            Log.Write("app icon unavailable: " + ex.Message);
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>A double-click, or any burst of clicks inside the system double-click interval, toggles exactly once.</summary>
    private void ToggleOnce()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastClickToggle).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
        _lastClickToggle = now;
        _toggleAction();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
