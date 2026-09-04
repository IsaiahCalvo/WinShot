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
    private static WF.Cursor? _camera;

    /// <summary>
    /// The window-pick cursor: a camera, so "click here" reads as "photograph this window"
    /// rather than the generic hand a link would use.
    /// </summary>
    public static WF.Cursor Camera => _camera ??= BuildCamera();

    private static WF.Cursor BuildCamera()
    {
        try
        {
            using var bmp = new SD.Bitmap(32, 32, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.Clear(SD.Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var font = ThemePalette.IconFont(ThemePalette.GlyphActionPt);
                // The glyph sits down-and-right of the image origin because a cursor built
                // from GetHicon carries a (0,0) hotspot — so the pointed-at pixel is the
                // top-left corner, and the camera trails it the way a drag ghost would.
                var box = new SD.Rectangle(4, 4, 28, 28);
                WF.TextRenderer.DrawText(g, "", font, box, SD.Color.Black,
                    WF.TextFormatFlags.NoPadding);
                WF.TextRenderer.DrawText(g, "", font,
                    new SD.Rectangle(box.X - 1, box.Y - 1, box.Width, box.Height), SD.Color.White,
                    WF.TextFormatFlags.NoPadding);
            }

            // One cursor for the process lifetime; the GDI handle is freed at exit.
            return new WF.Cursor(bmp.GetHicon());
        }
        catch (Exception ex)
        {
            Log.Error("Camera cursor could not be built; using the hand cursor", ex);
            return WF.Cursors.Hand;
        }
    }
}
