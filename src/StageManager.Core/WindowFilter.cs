namespace StageManager.Core;

/// <summary>Decides which top-level windows are managed, mirroring the rules Alt-Tab uses.</summary>
public static class WindowFilter
{
    private static readonly HashSet<string> BlockedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Xaml_WindowedPopupClass",
        "Windows.Internal.Shell.TabProxyWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "TaskListThumbnailWnd", "tooltips_class32", "SysShadow", "Shell_InputSwitchTopLevelWindow",
        "Shell_LightDismissOverlay", "PopupHost", "DesktopWindowXamlSource",
    };

    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "SearchHost", "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost",
        "LockApp", "PeopleExperienceHost", "ScreenClippingHost", "Widgets", "WidgetService",
    };

    public static bool IsManageable(WindowProbe p)
    {
        if (!p.IsVisible || p.IsChild || p.IsCloaked || p.IsOwnProcess) return false;
        if (!p.IsRootOwner) return false;
        if (p.IsToolWindow && !p.IsAppWindow) return false;
        if (p.IsNoActivate && !p.IsAppWindow) return false;
        if (string.IsNullOrWhiteSpace(p.Title)) return false;
        if (BlockedClasses.Contains(p.ClassName)) return false;
        if (BlockedProcesses.Contains(p.ProcessName)) return false;
        return true;
    }
}
