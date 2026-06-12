using SD = System.Drawing;

namespace WinShot.Core;

internal static class TrayIconFactory
{
    /// <summary>Draws the tray icon at runtime so the repo needs no binary assets.</summary>
    public static SD.Icon Create()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var body = new SD.SolidBrush(SD.Color.FromArgb(0x2D, 0x7D, 0xFF));
            g.FillEllipse(body, 1, 1, 30, 30);
            using var ring = new SD.Pen(SD.Color.White, 3f);
            g.DrawEllipse(ring, 9, 9, 14, 14);
        }

        // One icon for the app's lifetime; the GDI handle is freed at process exit.
        return SD.Icon.FromHandle(bmp.GetHicon());
    }
}
