using System.IO;
using System.Text.Json;
using StageManager.Core;

namespace StageManager.App;

/// <summary>
/// Remembers which windows we minimized so that, if the process dies, the next start can bring them back.
/// The file only exists while Stage Manager is enabled.
/// </summary>
internal sealed class ParkedWindowStore
{
    private readonly string _path = Path.Combine(Log.Directory, "parked.json");

    /// <summary>
    /// What the file holds right now, or null when there is no file. The engine raises Changed for title changes
    /// and the like many times a minute; only a different set of parked windows is worth a write.
    /// </summary>
    private string? _written;

    public void Save(IReadOnlyList<WindowId> parked)
    {
        try
        {
            if (parked.Count == 0) { Clear(); return; }
            var json = JsonSerializer.Serialize(parked.Select(p => (long)p.Value).Order().ToArray());
            if (json == _written) return;
            File.WriteAllText(_path, json);
            _written = json;
        }
        catch (Exception ex)
        {
            Log.Write("store save failed: " + ex.Message);
        }
    }

    public void Clear()
    {
        _written = null;
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* best effort */ }
    }

    public void RestoreAfterCrash(IWindowSystem ws)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var ids = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_path)) ?? [];
            int restored = 0;
            foreach (var value in ids)
            {
                var id = new WindowId((nint)value);
                if (ws.GetWindowInfo(id) is { IsMinimized: true })
                {
                    ws.RestoreNoActivate(id);
                    restored++;
                }
            }
            Log.Write($"restored {restored} window(s) left parked by a previous session");
            Clear();
        }
        catch (Exception ex)
        {
            Log.Write("crash restore failed: " + ex.Message);
        }
    }
}
