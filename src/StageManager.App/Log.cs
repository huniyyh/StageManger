using System.IO;

namespace StageManager.App;

internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Directory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StageManager");

    public static void Init()
    {
        System.IO.Directory.CreateDirectory(Directory);
        _path = Path.Combine(Directory, "stagemanager.log");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* logging must never take the app down */ }
        }
    }
}
