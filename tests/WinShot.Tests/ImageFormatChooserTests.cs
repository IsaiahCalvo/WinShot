using System.Drawing.Imaging;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// "Automatic" file format. The rule it has to get right is not the byte count — it is that
/// a capture with transparency never becomes a JPEG, because JPEG would silently flatten the
/// alpha onto black.
/// </summary>
public class ImageFormatChooserTests
{
    private static SD.Bitmap Flat(int w = 200, int h = 200)
    {
        // A screenshot's worth of flat UI: a few large blocks and some text-like strokes.
        var bmp = new SD.Bitmap(w, h, PixelFormat.Format32bppArgb);
        using SD.Graphics g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(255, 28, 28, 30));
        g.FillRectangle(SD.Brushes.White, 10, 10, w - 20, 30);
        using var pen = new SD.Pen(SD.Color.FromArgb(255, 10, 132, 255), 2);
        for (int y = 60; y < h - 10; y += 12)
            g.DrawLine(pen, 12, y, w - 12, y);
        return bmp;
    }

    private static SD.Bitmap Photographic(int w = 200, int h = 200)
    {
        // Every pixel a slightly different colour: what a photo or a heavy gradient looks
        // like to a colour-count heuristic.
        var bmp = new SD.Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rng = new Random(7);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bmp.SetPixel(x, y, SD.Color.FromArgb(
                    255,
                    (x * 255 / w + rng.Next(16)) & 0xFF,
                    (y * 255 / h + rng.Next(16)) & 0xFF,
                    ((x + y) * 255 / (w + h) + rng.Next(16)) & 0xFF));
            }
        }
        return bmp;
    }

    [Fact]
    public void AnythingButAutomaticPassesStraightThrough()
    {
        using SD.Bitmap bmp = Photographic();
        Assert.Equal("png", ImageFormatChooser.Resolve("png", bmp));
        Assert.Equal("jpg", ImageFormatChooser.Resolve("jpg", bmp));
        Assert.Equal("webp", ImageFormatChooser.Resolve("webp", bmp));
        // A missing or unset value is the shipped default, not an invitation to guess.
        Assert.Equal("png", ImageFormatChooser.Resolve(null, bmp));
        Assert.Equal("png", ImageFormatChooser.Resolve("  ", bmp));
    }

    [Fact]
    public void AFlatInterfaceCaptureStaysPng()
    {
        using SD.Bitmap bmp = Flat();
        Assert.Equal("png", ImageFormatChooser.Choose(bmp));
        Assert.Equal("png", ImageFormatChooser.Resolve("auto", bmp));
        Assert.Equal("png", ImageFormatChooser.Resolve("AUTO", bmp));
    }

    [Fact]
    public void APhotographicCaptureBecomesJpeg()
    {
        using SD.Bitmap bmp = Photographic();
        Assert.Equal("jpg", ImageFormatChooser.Choose(bmp));
    }

    [Fact]
    public void TransparencyIsNeverHandedToJpeg()
    {
        // The one that would destroy data: JPEG has no alpha, so a rounded-corner window
        // grab or an editor export over "None" must stay PNG however photographic it is.
        using SD.Bitmap bmp = Photographic();
        bmp.SetPixel(0, 0, SD.Color.FromArgb(0, 0, 0, 0));
        Assert.Equal("png", ImageFormatChooser.Choose(bmp));
    }

    [Fact]
    public void ADegenerateBitmapDoesNotThrow()
    {
        using var one = new SD.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        Assert.Equal("png", ImageFormatChooser.Choose(one));
    }

    [Fact]
    public void ALargeCaptureIsSampled_NotWalkedPixelByPixel()
    {
        // 4K at 150% is the normal case on Isaiah's machine; the choice must not cost a
        // full-image GetPixel walk before every save.
        using SD.Bitmap big = Flat(3840, 2160);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("png", ImageFormatChooser.Choose(big));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("Auto", true)]
    [InlineData("png", false)]
    [InlineData(null, false)]
    public void IsAutoRecognisesTheOptionInEitherCase(string? configured, bool expected)
    {
        Assert.Equal(expected, ImageFormatChooser.IsAuto(configured));
    }
}
