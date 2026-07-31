using System.Windows;
using SD = System.Drawing;

namespace WinShot.History;

internal static class HistoryWindowPlacement
{
    public static SD.Rectangle CenterInWorkArea(SD.Rectangle workAreaPixels, Size windowSizeDip, double dpiScale)
    {
        double safeScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        int width = Math.Min(workAreaPixels.Width, Math.Max(1, (int)Math.Round(windowSizeDip.Width * safeScale)));
        int height = Math.Min(workAreaPixels.Height, Math.Max(1, (int)Math.Round(windowSizeDip.Height * safeScale)));
        int left = workAreaPixels.Left + (workAreaPixels.Width - width) / 2;
        int top = workAreaPixels.Top + (workAreaPixels.Height - height) / 2;
        return new SD.Rectangle(left, top, width, height);
    }
}
