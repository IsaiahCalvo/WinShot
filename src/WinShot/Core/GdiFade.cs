using System.Drawing.Imaging;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Fades a piece of GDI/GDI+ chrome that contains text. GDI's <c>TextRenderer</c> drops the
/// alpha channel, so handing it an alpha-scaled colour fades nothing: only the GDI+ fill
/// underneath dissolves and the ink stays solid until it pops out of existence. The way out is
/// to paint the whole thing once into an ARGB buffer and blit that buffer through a
/// <see cref="ColorMatrix"/>, which is what this does.
/// </summary>
internal static class GdiFade
{
    /// <summary>Everything at or above this reads as opaque; painting it straight to the
    /// target skips a buffer and keeps the resting frame pixel-identical to no fade at all.</summary>
    private const double Opaque = 0.998;

    /// <summary>
    /// Runs <paramref name="paint"/> at <paramref name="amount"/> opacity inside
    /// <paramref name="bounds"/>. The painter is handed the rectangle to draw into — the
    /// caller's own <paramref name="bounds"/> when nothing is being faded, and the buffer's
    /// origin-based equivalent when it is — so it must lay out relative to that rectangle.
    /// </summary>
    public static void Draw(
        SD.Graphics g,
        SD.Rectangle bounds,
        double amount,
        Action<SD.Graphics, SD.Rectangle> paint)
    {
        double a = Math.Clamp(amount, 0, 1);
        if (a <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (a >= Opaque)
        {
            paint(g, bounds);
            return;
        }

        using var buffer = new SD.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (SD.Graphics bg = SD.Graphics.FromImage(buffer))
            paint(bg, new SD.Rectangle(0, 0, bounds.Width, bounds.Height));

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)a });
        g.DrawImage(
            buffer, bounds, 0, 0, bounds.Width, bounds.Height, SD.GraphicsUnit.Pixel, attributes);
    }
}
