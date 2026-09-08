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
            using SD.Bitmap bmp = BuildCameraBitmap(scale);
            // GetHicon hands back a handle the caller owns, and Cursor(IntPtr) does not take
            // ownership — so this handle lives until the process does. That is deliberate,
            // not a leak that grows: the cache key is the clamped scale, so a run holds one
            // HICON per distinct monitor scale (100/125/150/175/200 in practice) and every
            // one of them is still in use by a live Cursor. Destroying it here would blank
            // the cursor; there is nowhere later to destroy it from.
            return new WF.Cursor(bmp.GetHicon());
        }
        catch (Exception ex)
        {
            Log.Error("Camera cursor could not be built; using the hand cursor", ex);
            return WF.Cursors.Hand;
        }
    }

    /// <summary>
    /// The cursor image before it becomes an HCURSOR, exposed so a test can read its pixels.
    ///
    /// The glyph goes through GDI+ <c>DrawString</c>, never <c>TextRenderer.DrawText</c>.
    /// TextRenderer is a GDI (DrawTextW) wrapper and GDI does not write the alpha byte of a
    /// 32-bpp DIB, so a glyph drawn that way onto a surface cleared to Transparent stays at
    /// A=0 — and <c>GetHicon</c> derives the icon's AND mask from alpha, so the cursor came
    /// out invisible (or, if GDI+ forced A=255 on ReleaseHdc, a black square trailing the
    /// pointer). <see cref="SelectorChrome"/> documents the same trap for the fading labels.
    /// </summary>
    internal static SD.Bitmap BuildCameraBitmap(double scale)
    {
        int size = WinShot.Recording.RecordingMonitorDpi.Px(CameraLogicalPx, scale);
        int inset = WinShot.Recording.RecordingMonitorDpi.Px(4, scale);
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g = SD.Graphics.FromImage(bmp);
            g.Clear(SD.Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            // The glyph font is in POINTS, which resolve against the bitmap's own 96 DPI
            // and not the target monitor's, so the point size carries the scale too.
            using var font = ThemePalette.IconFont((float)(ThemePalette.GlyphActionPt * scale));
            using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic)
            {
                Alignment = SD.StringAlignment.Near,
                LineAlignment = SD.StringAlignment.Near,
                FormatFlags = SD.StringFormatFlags.NoWrap,
            };
            // The glyph sits down-and-right of the image origin because a cursor built
            // from GetHicon carries a (0,0) hotspot -- so the pointed-at pixel is the
            // top-left corner, and the camera trails it the way a drag ghost would.
            var box = GlyphBox(size, inset);
            // A symmetric halo, not one offset copy: a single dark pass down-and-right left
            // the top-left edge of a white camera with no contrast at all over light screen
            // content, and the cursor has to read on any wallpaper.
            using (var halo = new SD.SolidBrush(SD.Color.Black))
            {
                foreach (var (dx, dy) in Halo)
                    g.DrawString(CameraGlyph, font, halo, box.X + dx, box.Y + dy, format);
            }
            using (var brush = new SD.SolidBrush(SD.Color.White))
                g.DrawString(CameraGlyph, font, brush, box.X, box.Y, format);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>The glyph's content box inside a <paramref name="size"/>-px cursor bitmap:
    /// inset on all four sides, so the extent loses TWO insets, not one. As
    /// <c>size - inset</c> the box ran one inset past the bitmap on the right and bottom —
    /// invisible while only the origin is read, a silent shift the moment anything centres
    /// in it.</summary>
    internal static SD.Rectangle GlyphBox(int size, int inset) =>
        new(inset, inset, Math.Max(1, size - inset * 2), Math.Max(1, size - inset * 2));

    /// <summary>Segoe Fluent Icons "Camera" (U+E722), the same family every other glyph uses.</summary>
    private const string CameraGlyph = "";

    private static readonly (int dx, int dy)[] Halo =
        [(-1, 0), (1, 0), (0, -1), (0, 1)];
}
