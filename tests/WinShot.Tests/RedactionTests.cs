using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class RedactionTests
{
    private static SD.Bitmap Noise(int w = 64, int h = 48)
    {
        var bmp = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppArgb);
        var rng = new Random(4242);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, SD.Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)));
        return bmp;
    }

    private static byte[] Bytes(SD.Bitmap bmp)
    {
        var bytes = new byte[bmp.Width * bmp.Height * 4];
        int i = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SD.Color c = bmp.GetPixel(x, y);
                bytes[i++] = c.A; bytes[i++] = c.R; bytes[i++] = c.G; bytes[i++] = c.B;
            }
        return bytes;
    }

    [Fact]
    public void StrengthMapsAcrossItsWholeRangeAndClampsOutsideIt()
    {
        Assert.Equal(Redaction.MinStrength, Redaction.ClampStrength(-5));
        Assert.Equal(Redaction.MaxStrength, Redaction.ClampStrength(9999));

        // Monotonic, and the two ends are meaningfully different — a slider whose ends
        // produce the same picture is a slider nobody can use.
        Assert.True(Redaction.CellFor(Redaction.MinStrength) < Redaction.CellFor(Redaction.MaxStrength));
        Assert.True(Redaction.RadiusFor(Redaction.MinStrength) < Redaction.RadiusFor(Redaction.MaxStrength));
        Assert.True(Redaction.CellFor(Redaction.MaxStrength) >= 25);
        Assert.True(Redaction.RadiusFor(Redaction.MaxStrength) >= 20);
        for (int s = Redaction.MinStrength; s < Redaction.MaxStrength; s++)
        {
            Assert.True(Redaction.CellFor(s) <= Redaction.CellFor(s + 1));
            Assert.True(Redaction.RadiusFor(s) <= Redaction.RadiusFor(s + 1));
        }
    }

    /// <summary>
    /// Black Out is the one style with nothing to dial, which is why the slider hides for
    /// it instead of greying out — and why Render refuses to produce pixels for it.
    /// </summary>
    [Fact]
    public void BlackOutCarriesNoStrengthAndRendersNoPixels()
    {
        Assert.False(Redaction.UsesStrength(RedactStyle.BlackOut));
        foreach (RedactStyle style in new[] { RedactStyle.Pixelate, RedactStyle.BlurSecure, RedactStyle.BlurSmooth })
            Assert.True(Redaction.UsesStrength(style));

        using SD.Bitmap source = Noise();
        Assert.Null(Redaction.Render(source, new SD.Rectangle(4, 4, 20, 20), RedactStyle.BlackOut, 50, 1));
    }

    [Fact]
    public void EveryStyleActuallyChangesThePixelsItCovers()
    {
        using SD.Bitmap source = Noise();
        var region = new SD.Rectangle(8, 8, 32, 24);
        using SD.Bitmap original = WinShot.Core.CaptureService.Crop(source, region);
        byte[] before = Bytes(original);

        foreach (RedactStyle style in new[] { RedactStyle.Pixelate, RedactStyle.BlurSecure, RedactStyle.BlurSmooth })
        {
            using SD.Bitmap? rendered = Redaction.Render(source, region, style, 60, seed: 11);
            Assert.NotNull(rendered);
            Assert.Equal(region.Width, rendered!.Width);
            Assert.Equal(region.Height, rendered.Height);
            Assert.NotEqual(before, Bytes(rendered));
        }
    }

    /// <summary>
    /// The seed rides in the annotation's metadata so a mosaic redraws with the same
    /// blocks after a move, an undo, or a project reload. Without it, every recompute
    /// would reshuffle the squares and the mark would visibly shimmer as you drag it.
    /// </summary>
    [Fact]
    public void TheSameSeedRedrawsTheSameMosaic()
    {
        using SD.Bitmap source = Noise();
        var region = new SD.Rectangle(2, 2, 40, 30);

        using SD.Bitmap? a = Redaction.Render(source, region, RedactStyle.Pixelate, 40, seed: 909);
        using SD.Bitmap? b = Redaction.Render(source, region, RedactStyle.Pixelate, 40, seed: 909);
        using SD.Bitmap? c = Redaction.Render(source, region, RedactStyle.Pixelate, 40, seed: 910);

        Assert.Equal(Bytes(a!), Bytes(b!));
        Assert.NotEqual(Bytes(a!), Bytes(c!));
    }

    /// <summary>
    /// A redaction dragged half off the canvas must keep covering what it covers. The
    /// tile is always the mark's own size, with the off-image part left black — stretching
    /// the surviving pixels across the whole mark would smear them into legibility.
    /// </summary>
    [Fact]
    public void AMarkHangingOffTheImageIsPaddedBlackRatherThanStretched()
    {
        using SD.Bitmap source = Noise(40, 40);
        var region = new SD.Rectangle(30, 30, 30, 30); // only the top-left 10x10 is on the image

        using SD.Bitmap? tile = Redaction.Render(source, region, RedactStyle.Pixelate, 50, seed: 5);
        Assert.NotNull(tile);
        Assert.Equal(30, tile!.Width);
        Assert.Equal(30, tile.Height);
        Assert.Equal(SD.Color.Black.ToArgb(), tile.GetPixel(25, 25).ToArgb());

        // Entirely off the image: nothing to render, and the caller paints flat black.
        Assert.Null(Redaction.Render(source, new SD.Rectangle(80, 80, 10, 10), RedactStyle.Pixelate, 50, 5));
        Assert.Null(Redaction.Render(source, new SD.Rectangle(0, 0, 0, 0), RedactStyle.Pixelate, 50, 5));
    }

    /// <summary>
    /// A plain blur is a convolution and can be partly inverted; the "secure" variant
    /// destroys the information with a randomized mosaic first, so it must not agree with
    /// the smooth one on the same input.
    /// </summary>
    [Fact]
    public void SecureBlurIsNotJustTheSmoothBlur()
    {
        using SD.Bitmap source = Noise();
        var region = new SD.Rectangle(4, 4, 40, 30);

        using SD.Bitmap? secure = Redaction.Render(source, region, RedactStyle.BlurSecure, 55, seed: 3);
        using SD.Bitmap? smooth = Redaction.Render(source, region, RedactStyle.BlurSmooth, 55, seed: 3);
        Assert.NotEqual(Bytes(secure!), Bytes(smooth!));
    }

    [Fact]
    public void UnknownStoredStyleFallsBackToPixelateRatherThanThrowing()
    {
        Assert.Equal(RedactStyle.Pixelate, Redaction.Parse(null));
        Assert.Equal(RedactStyle.Pixelate, Redaction.Parse("NotAStyle"));
        Assert.Equal(RedactStyle.BlackOut, Redaction.Parse("BlackOut"));
    }

    [Fact]
    public void ProjectDataRoundTripsStyleStrengthAndSeed()
    {
        var data = AnnotationData.ForRedaction(
            new System.Windows.Rect(3, 4, 20, 10), RedactStyle.BlurSecure, strength: 500, seed: 77);

        Assert.Equal(AnnotationData.TypeRedact, data.Type);
        Assert.Equal("BlurSecure", data.Style);
        Assert.Equal(Redaction.MaxStrength, data.Strength); // clamped on the way in
        Assert.Equal(77, data.Seed);
        Assert.Equal(new[] { 3d, 4d, 20d, 10d }, data.Rect);
    }
}
