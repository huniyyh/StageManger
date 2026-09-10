using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StageManager.Win32;

/// <summary>A minimal message loop for console tools that need WinEvent hooks to fire.</summary>
public static class MessagePump
{
    public static void RunFor(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            while (PInvoke.PeekMessage(out MSG msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }
            Thread.Sleep(10);
        }
    }
}
