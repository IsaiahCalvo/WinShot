using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Cursors the selectors swap in to say what a click will do. Drawn at runtime from the
/// icon font, so the repo carries no .cur assets and the glyph tracks the app's icon family.
/// </summary>
internal static class SelectorCursors
{
    /// <summary>One cursor per monitor scale, not one per process. A custom HCURSOR is drawn
    /// by Windows at its native size in PHYSICAL pixels — unlike the system cursors it stands
    /// in for, it is never scaled for the monitor it lands on — so a single 32x32 build read
    /// two-thirds size against a 48 px arrow at 150% and half size at 200%.</summary>
    private static readonly Dictionary<int, WF.Cursor> _cameras = [];

    /// <summary>Logical size of the camera cursor, before the monitor scale.</summary>
    private const int CameraLogicalPx = 32;

    /// <summary>
    /// The window-pick cursor: a camera, so "click here" reads as "photograph this window"
    /// rather than the generic hand a link would use. <paramref name="scale"/> is the
    /// selector monitor's DPI factor (RecordingMonitorDpi.ScaleFor) — AutoScaleMode.Dpi never
    /// scales anything here, so every surface multiplies by hand.
    /// </summary>
    public static WF.Cursor Camera(double scale)
    {
        int key = Math.Clamp((int)Math.Round(scale * 100), 100, 400);
        lock (_cameras)
        {
            if (_cameras.TryGetValue(key, out var cached))
                return cached;
            WF.Cursor built = BuildCamera(key / 100.0);
            _cameras[key] = built;
            return built;
        }
    }

    private static WF.Cursor BuildCamera(double scale)
    {
        try
        {
            int size = WinShot.Recording.RecordingMonitorDpi.Px(CameraLogicalPx, scale);
            int inset = WinShot.Recording.RecordingMonitorDpi.Px(4, scale);
            using var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.Clear(SD.Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                // The glyph font is in POINTS, which resolve against the bitmap's own 96 DPI
                // and not the target monitor's, so the point size carries the scale too.
                using var font = ThemePalette.IconFont((float)(ThemePalette.GlyphActionPt * scale));
                // The glyph sits down-and-right of the image origin because a cursor built
                // from GetHicon carries a (0,0) hotspot -- so the pointed-at pixel is the
                // top-left corner, and the camera trails it the way a drag ghost would.
                var box = new SD.Rectangle(inset, inset, size - inset, size - inset);
                WF.TextRenderer.DrawText(g, "", font, box, SD.Color.Black,
                    WF.TextFormatFlags.NoPadding);
                WF.TextRenderer.DrawText(g, "", font,
                    new SD.Rectangle(box.X - 1, box.Y - 1, box.Width, box.Height), SD.Color.White,
                    WF.TextFormatFlags.NoPadding);
            }

            // One cursor per scale for the process lifetime; the GDI handle is freed at exit.
            return new WF.Cursor(bmp.GetHicon());
        }
        catch (Exception ex)
        {
            Log.Error("Camera cursor could not be built; using the hand cursor", ex);
            return WF.Cursors.Hand;
        }
    }
}
