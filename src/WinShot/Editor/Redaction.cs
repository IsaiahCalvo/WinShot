using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Editor;

/// <summary>
/// How a redaction hides what is under it. Stored in <c>AnnotationData.Style</c>, so a
/// redaction can be restyled long after it was drawn.
/// </summary>
internal enum RedactStyle
{
    /// <summary>Randomized mosaic. Jittered per cell, so the text cannot be reconstructed.</summary>
    Pixelate,

    /// <summary>Mosaic first, then smoothed — soft to look at, still non-invertible.</summary>
    BlurSecure,

    /// <summary>A plain blur. The gentle look; readable shapes survive, so it is not a redaction of last resort.</summary>
    BlurSmooth,

    /// <summary>Flat black. Nothing is left to recover, and strength means nothing.</summary>
    BlackOut,
}

/// <summary>
/// The pixels behind a redaction annotation. The mark itself stays a live, movable,
/// re-styleable object on the canvas — this only produces the picture it is filled with,
/// recomputed whenever the mark moves, resizes, or changes style or strength, and baked
/// into the export by the normal Flatten() render.
/// </summary>
internal static class Redaction
{
    public const int MinStrength = 1;
    public const int MaxStrength = 100;
    public const int DefaultStrength = 45;

    public static int ClampStrength(int strength) => Math.Clamp(strength, MinStrength, MaxStrength);

    /// <summary>Mosaic cell size (px) — 2 at the low end, 30 at the high end.</summary>
    public static int CellFor(int strength) => 2 + (int)Math.Round(ClampStrength(strength) * 0.28);

    /// <summary>Box-blur radius (px) — 1 at the low end, 25 at the high end.</summary>
    public static int RadiusFor(int strength) => 1 + (int)Math.Round(ClampStrength(strength) * 0.24);

    /// <summary>
    /// Whether the strength slider means anything for this style. Black Out has no
    /// strength, so the slider HIDES rather than greying out and the toolbar shrinks.
    /// </summary>
    public static bool UsesStrength(RedactStyle style) => style != RedactStyle.BlackOut;

    public static RedactStyle Parse(string? name) =>
        Enum.TryParse(name, out RedactStyle style) ? style : RedactStyle.Pixelate;

    /// <summary>
    /// The image that fills a redaction covering <paramref name="region"/> of
    /// <paramref name="source"/>, in source pixels. The bitmap is always exactly
    /// <paramref name="region"/>'s size: any part of the region hanging off the image is
    /// left black, so dragging a redaction half off the canvas keeps covering what it
    /// covered instead of stretching the remaining pixels across the whole mark.
    /// Returns null for a degenerate region; the caller paints flat black.
    /// </summary>
    public static SD.Bitmap? Render(
        SD.Bitmap source, SD.Rectangle region, RedactStyle style, int strength, int seed)
    {
        if (region.Width < 1 || region.Height < 1) return null;
        if (style == RedactStyle.BlackOut) return null;

        SD.Rectangle inside = SD.Rectangle.Intersect(
            region, new SD.Rectangle(0, 0, source.Width, source.Height));
        if (inside.Width < 1 || inside.Height < 1) return null;

        SD.Bitmap crop = CaptureService.Crop(source, inside);
        try
        {
            var whole = new SD.Rectangle(0, 0, crop.Width, crop.Height);
            switch (style)
            {
                case RedactStyle.Pixelate:
                    BitmapEffects.PixelateRandomized(crop, whole, seed, CellFor(strength));
                    break;
                case RedactStyle.BlurSmooth:
                    BitmapEffects.Blur(crop, whole, RadiusFor(strength));
                    break;
                case RedactStyle.BlurSecure:
                    // A blur is a convolution and can be partly inverted; a randomized
                    // mosaic cannot. Destroy the information first, then smooth the mosaic
                    // so it still reads as a blur rather than a grid of squares.
                    BitmapEffects.PixelateRandomized(crop, whole, seed, Math.Max(3, CellFor(strength)));
                    BitmapEffects.Blur(crop, whole, Math.Max(2, RadiusFor(strength) / 2));
                    break;
            }

            if (inside == region)
                return crop;

            // Part of the mark hangs off the image: compose onto a black tile of the mark's
            // own size so the covered pixels stay 1:1 under it.
            var tile = new SD.Bitmap(region.Width, region.Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(tile))
            {
                g.Clear(SD.Color.Black);
                g.DrawImageUnscaled(crop, inside.X - region.X, inside.Y - region.Y);
            }
            crop.Dispose();
            return tile;
        }
        catch
        {
            crop.Dispose();
            throw;
        }
    }
}
