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

    public void Save(IReadOnlyList<WindowId> parked)
    {
        try
        {
            if (parked.Count == 0) { Clear(); return; }
            File.WriteAllText(_path, JsonSerializer.Serialize(parked.Select(p => (long)p.Value).ToArray()));
        }
        catch (Exception ex)
        {
            Log.Write("store save failed: " + ex.Message);
        }
    }

    public void Clear()
    {
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
