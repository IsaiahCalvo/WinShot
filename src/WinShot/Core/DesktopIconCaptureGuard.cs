namespace WinShot.Core;

public static class DesktopIconCaptureGuard
{
    public static T Run<T>(
        bool hideDuringCapture,
        Func<bool> iconsVisible,
        Action hideIcons,
        Action showIcons,
        Action<int> wait,
        Func<T> capture,
        int repaintDelayMs = 120)
    {
        bool hid = false;
        if (hideDuringCapture && iconsVisible())
        {
            hideIcons();
            hid = true;
            wait(repaintDelayMs);
        }

        try
        {
            return capture();
        }
        finally
        {
            if (hid)
                showIcons();
        }
    }
}
