using System.Management;
using System.Windows.Media;

namespace StageManager.App;

/// <summary>
/// What the machine can render comfortably. Virtual machines and remote sessions often have no GPU, yet WPF may
/// still report a hardware tier there (a software Direct3D device answers the capability query), so the adapter
/// name is checked as well. STAGEMANAGER_NO_EFFECTS=1 forces the lean path.
/// </summary>
internal static class Visuals
{
    private static readonly string[] UnacceleratedAdapters =
    {
        "VirtIO", "Microsoft Basic Render", "Microsoft Basic Display", "Microsoft Hyper-V", "VMware SVGA",
        "QXL", "Remote Display", "RDP", "Parallels", "VirtualBox", "Citrix",
    };

    /// <summary>True when pixel-shader effects would be computed on the CPU for every frame.</summary>
    public static bool SoftwareRendering { get; }

    /// <summary>For the log: what was decided and why.</summary>
    public static string Description { get; }

    static Visuals()
    {
        int tier = RenderCapability.Tier >> 16;
        string adapters = string.Join(", ", AdapterNames());
        bool forced = Environment.GetEnvironmentVariable("STAGEMANAGER_NO_EFFECTS") == "1";
        bool unaccelerated = UnacceleratedAdapters.Any(name => adapters.Contains(name, StringComparison.OrdinalIgnoreCase));
        SoftwareRendering = forced || tier == 0 || unaccelerated;
        Description = $"render tier {tier}, adapters [{adapters}]{(forced ? ", effects forced off" : "")} -> {(SoftwareRendering ? "software" : "hardware")}";
    }

    private static IEnumerable<string> AdapterNames()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var adapter in searcher.Get())
                if (adapter["Name"] is string name) names.Add(name);
        }
        catch (Exception ex)
        {
            names.Add("unknown: " + ex.Message);
        }
        return names;
    }
}
