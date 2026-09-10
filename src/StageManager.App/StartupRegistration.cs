using System.IO;
using Microsoft.Win32;

namespace StageManager.App;

/// <summary>
/// Runs the app at logon through the per-user Run key, so no elevation is needed and the entry shows up in
/// Settings > Apps > Startup. Installed builds register Velopack's launcher stub, which keeps working across updates.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StageManager";
    private const string EnableSwitch = "--enable";

    /// <summary>The exe to launch at logon: Velopack's stub next to the versioned "current" folder when installed, else this process.</summary>
    public static string LauncherPath
    {
        get
        {
            var exe = Environment.ProcessPath ?? "";
            var dir = Path.GetDirectoryName(exe);
            if (dir != null && string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.Combine(Path.GetDirectoryName(dir)!, Path.GetFileName(exe));
                if (File.Exists(stub)) return stub;
            }
            return exe;
        }
    }

    /// <summary>Whether we run at logon, and whether we start with Stage Manager turned on.</summary>
    public static (bool RunAtLogon, bool StartEnabled) Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string command) return (false, false);
            return (true, command.Contains(EnableSwitch, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Write("startup registration could not be read: " + ex.Message);
            return (false, false);
        }
    }

    public static void Write(bool runAtLogon, bool startEnabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (!runAtLogon)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("startup: not running at logon");
                return;
            }
            var command = $"\"{LauncherPath}\"" + (startEnabled ? " " + EnableSwitch : "");
            key.SetValue(ValueName, command);
            Log.Write("startup: " + command);
        }
        catch (Exception ex)
        {
            Log.Write("startup registration failed: " + ex.Message);
        }
    }

    /// <summary>Called by the uninstaller so no dangling entry is left behind.</summary>
    public static void Remove() => Write(runAtLogon: false, startEnabled: false);
}
