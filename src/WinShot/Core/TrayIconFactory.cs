using SD = System.Drawing;

namespace WinShot.Core;

internal static class TrayIconFactory
{
    // Two icons for the app's lifetime, cached because the tray swaps between them on every
    // recording: Icon.FromHandle does not own its HICON, so drawing a fresh one per swap
    // would leak a GDI handle per take. The handles are freed at process exit.
    private static SD.Icon? _idle;
    private static SD.Icon? _recording;

    /// <summary>Draws the tray icon at runtime so the repo needs no binary assets.</summary>
    public static SD.Icon Create() => _idle ??= Draw(recording: false);

    /// <summary>The icon shown while a take is running: the same disc wearing a stop square.
    /// Double-clicking the tray while it shows this stops and saves the recording (App wires
    /// that), so the square is an action and not only a status light.</summary>
    public static SD.Icon CreateRecording() => _recording ??= Draw(recording: true);

    private static SD.Icon Draw(bool recording)
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var body = new SD.SolidBrush(recording ? ThemePalette.Red : ThemePalette.Accent);
            g.FillEllipse(body, 1, 1, 30, 30);
            if (recording)
            {
                // A filled square is the stop glyph at any size; a ring would read as the
                // idle icon tinted red at 16 px.
                using var stop = new SD.SolidBrush(SD.Color.White);
                using var path = GdiPaths.RoundedRect(new SD.Rectangle(10, 10, 12, 12), 2);
                g.FillPath(stop, path);
            }
            else
            {
                using var ring = new SD.Pen(SD.Color.White, 3f);
                g.DrawEllipse(ring, 9, 9, 14, 14);
            }
        }

        return SD.Icon.FromHandle(bmp.GetHicon());
    }
}
