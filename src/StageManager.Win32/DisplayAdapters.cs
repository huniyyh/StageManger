using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace StageManager.Win32;

/// <summary>
/// Names of the display adapters, straight from EnumDisplayDevices. Answers in microseconds and needs no extra
/// assembly, unlike a WMI query for Win32_VideoController, which can take hundreds of milliseconds at startup
/// and pulls in System.Management.
/// </summary>
public static class DisplayAdapters
{
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    /// <summary>
    /// Distinct names of the adapters driving the desktop, which are the ones WPF renders through. Adapters that
    /// are present but not attached to the desktop are only reported when no attached one is found, so a
    /// leftover driverless device does not count against a working GPU. Mirroring drivers are left out.
    /// </summary>
    public static unsafe IReadOnlyList<string> Names()
    {
        var attached = new List<string>();
        var others = new List<string>();
        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
            if (!PInvoke.EnumDisplayDevices(null, i, ref device, 0)) break;

            uint state = (uint)device.StateFlags;
            if ((state & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
            string name = device.DeviceString.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var list = (state & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 ? attached : others;
            if (!list.Contains(name)) list.Add(name);
        }
        return attached.Count > 0 ? attached : others;
    }
}
