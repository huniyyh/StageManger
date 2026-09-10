using System.Windows.Forms;

namespace StageManager.App;

/// <summary>Notification-area icon with the toggle and exit commands.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string EnableText = "Stage Manager 켜기  (Ctrl+Alt+S)";
    private const string DisableText = "Stage Manager 끄기  (Ctrl+Alt+S)";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggle;
    private readonly Action _toggleAction;
    private DateTime _lastClickToggle = DateTime.MinValue;

    public TrayIcon(Action toggle, Action exit)
    {
        _toggleAction = toggle;
        var menu = new ContextMenuStrip();
        _toggle = new ToolStripMenuItem(EnableText);
        _toggle.Click += (_, _) => toggle();
        var quit = new ToolStripMenuItem("종료");
        quit.Click += (_, _) => exit();
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);

        _icon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Stage Manager (꺼짐)",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleOnce(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleOnce(); };
    }

    /// <summary>A double-click, or any burst of clicks inside the system double-click interval, toggles exactly once.</summary>
    private void ToggleOnce()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastClickToggle).TotalMilliseconds < SystemInformation.DoubleClickTime) return;
        _lastClickToggle = now;
        _toggleAction();
    }

    public void Update(bool enabled)
    {
        _toggle.Text = enabled ? DisableText : EnableText;
        _icon.Text = enabled ? "Stage Manager (켜짐)" : "Stage Manager (꺼짐)";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
