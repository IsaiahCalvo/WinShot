using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// Composites a capture onto opaque white for the two sinks that cannot carry an alpha
/// channel and read the RGB underneath it instead: the JPEG encoder, and the clipboard's
/// CF_DIB payload.
/// </summary>
/// <remarks>
/// GDI+ leaves a cleared pixel at ARGB(0,0,0,0) — transparent BLACK. That is invisible
/// anywhere alpha is honoured and solid black everywhere it is not, which is why a window
/// capture with its rounded corners trimmed came out of a JPEG save, or a paste into MS
/// Paint, with four black wedges. White is the flatten colour because it is what every
/// viewer, browser and print path assumes under a transparent PNG.
///
/// The PNG clipboard format and PNG saves are untouched: they carry the real alpha, and
/// alpha-aware paste targets prefer them.
/// </remarks>
public static class AlphaFlatten
{
    /// <summary>
    /// The image over white, or null when it has no alpha channel to lose and the caller
    /// should use the original.
    /// </summary>
    // ponytail: copies any 32-bit-with-alpha bitmap, including a fully opaque one. Sampling
    // for a transparent pixel first would skip most copies; measure the clipboard path
    // before adding a heuristic that can miss the corner it is looking for.
    public static SD.Bitmap? OverWhite(SD.Bitmap source)
    {
        if (!SD.Image.IsAlphaPixelFormat(source.PixelFormat))
            return null;

        // 32bppArgb, not 32bppRgb: the DIB writer copies raw rows, and Format32bppRgb leaves
        // the unused byte at 0, which is a fully transparent alpha to anything that does read
        // it. Clearing to opaque white makes every pixel A=255.
        var flat = new SD.Bitmap(source.Width, source.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            flat.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using SD.Graphics g = SD.Graphics.FromImage(flat);
            g.Clear(SD.Color.White);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
            return flat;
        }
        catch
        {
            flat.Dispose();
            throw;
        }
    }
}
