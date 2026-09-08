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
            // PIXELS, not points. A point-unit font resolves against the surface's DPI, and a
            // new Bitmap inherits the PROCESS's — WinShot declares PerMonitorV2, so on a 150%
            // primary that is 144, not the 96 this code used to claim. The glyph therefore came
            // out 15*scale*144/72 = 30*scale px wide instead of 20*scale, and ran off the right
            // and bottom of its own bitmap at every scale: measured ink reached the last column
            // at 100/125/150/200%, cutting 3-5 px off the camera's right side. That is the
            // "right side of the icon is a little cut off" report. ThemePalette documents this
            // exact trap; this was the last selector surface still building a Point font.
            int em = ThemePalette.PxForPt(ThemePalette.GlyphActionPt, scale);
            using var font = ThemePalette.IconFontPx(em);
            using var format = new SD.StringFormat(SD.StringFormat.GenericTypographic)
            {
                Alignment = SD.StringAlignment.Center,
                LineAlignment = SD.StringAlignment.Center,
                // NoClip kept, not overwritten: GenericTypographic carries it, and without it
                // GDI+ hard-clips the string to the layout rect instead of letting a glyph whose
                // black box overhangs its advance width finish drawing.
                FormatFlags = SD.StringFormatFlags.NoWrap | SD.StringFormatFlags.NoClip,
            };
            // Centred in the box, not anchored at its top-left. Anchoring is how the clipping
            // survived: the box is inset equally on all four sides, but a glyph pinned to the
            // corner only ever consumes the LEFT inset and spends whatever the font asks for on
            // the right. The inset is the halo's room now, on all four sides.
            var box = GlyphBox(size, inset);
            // Centring the LAYOUT box is not centring the glyph: the box is the font's line
            // box (ascent plus descent) and the camera's ink sits above the baseline, so
            // layout-centred ink measured 6 px from the top and 8 from the bottom at 125%.
            // Two px of droop is exactly the kind of thing that reads as "off" without being
            // nameable, so the ink is measured and the box moved to put it in the middle.
            var (dx0, dy0) = CentringOffset(size, box, font, format);
            box.Offset(dx0, dy0);
            // A symmetric halo, not one offset copy: a single dark pass down-and-right left
            // the top-left edge of a white camera with no contrast at all over light screen
            // content, and the cursor has to read on any wallpaper.
            using (var halo = new SD.SolidBrush(SD.Color.Black))
            {
                foreach (var (dx, dy) in Halo)
                    g.DrawString(CameraGlyph, font, halo, Offset(box, dx, dy), format);
            }
            using (var brush = new SD.SolidBrush(SD.Color.White))
                g.DrawString(CameraGlyph, font, brush, box, format);
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

    private static SD.RectangleF Offset(SD.Rectangle box, int dx, int dy) =>
        new(box.X + dx, box.Y + dy, box.Width, box.Height);

    /// <summary>
    /// How far to move <paramref name="box"/> so the glyph's INK lands in the middle of a
    /// <paramref name="size"/>-px cursor. Draws once into a throwaway surface and reads the
    /// alpha bounds, because the offset between a font's line box and its ink is a property of
    /// the font that nothing else here can be told. Cheap: one draw per distinct monitor scale
    /// for the life of the process, and the cursors are cached.
    /// </summary>
    private static (int Dx, int Dy) CentringOffset(
        int size, SD.Rectangle box, SD.Font font, SD.StringFormat format)
    {
        using var probe = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(probe))
        {
            g.Clear(SD.Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var white = new SD.SolidBrush(SD.Color.White);
            g.DrawString(CameraGlyph, font, white, box, format);
        }

        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Same antialiasing floor the harness scans with, so the two agree on what
                // counts as ink.
                if (probe.GetPixel(x, y).A <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
            return (0, 0); // no glyph (missing font): leave the box where it is

        return ((size - 1 - maxX - minX) / 2, (size - 1 - maxY - minY) / 2);
    }

    /// <summary>Segoe Fluent Icons "Camera" (U+E722), the same family every other glyph uses.</summary>
    private const string CameraGlyph = "";

    private static readonly (int dx, int dy)[] Halo =
        [(-1, 0), (1, 0), (0, -1), (0, 1)];
}
