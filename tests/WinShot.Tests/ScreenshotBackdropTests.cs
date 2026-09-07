using System.Drawing.Imaging;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The standing "frame every shot like this" background. The invariants that matter are that
/// None changes nothing, that the capture is never lost to a failed backdrop, and that the
/// shot lands intact in the middle of the canvas rather than being scaled into it.
/// </summary>
public class ScreenshotBackdropTests
{
    private static SD.Bitmap Shot(int w = 120, int h = 80)
    {
        var bmp = new SD.Bitmap(w, h, PixelFormat.Format32bppArgb);
        using SD.Graphics g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(255, 240, 0, 120)); // a colour no backdrop uses
        return bmp;
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-else")]
    public void NoneLeavesTheCaptureAlone(string? mode)
    {
        using SD.Bitmap shot = Shot();
        Assert.False(ScreenshotBackdrop.IsEnabled(mode));
        Assert.Null(ScreenshotBackdrop.Compose(shot, mode, "#000000"));
    }

    [Theory]
    [InlineData("gradient")]
    [InlineData("color")]
    [InlineData("wallpaper")]
    [InlineData("GRADIENT")]
    public void EveryOtherModeComposes(string mode)
    {
        using SD.Bitmap shot = Shot();
        Assert.True(ScreenshotBackdrop.IsEnabled(mode));

        using SD.Bitmap? framed = ScreenshotBackdrop.Compose(shot, mode, "#204080");
        Assert.NotNull(framed);
        Assert.Equal(ScreenshotBackdrop.CanvasFor(shot.Size), framed!.Size);

        // The original is untouched — the caller still owns it and may still be using it.
        Assert.Equal(120, shot.Width);
        Assert.Equal(SD.Color.FromArgb(255, 240, 0, 120).ToArgb(), shot.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void TheShotIsPlacedWholeAndCentred_NotScaledToFit()
    {
        using SD.Bitmap shot = Shot();
        using SD.Bitmap framed = ScreenshotBackdrop.Compose(shot, "color", "#204080")!;
        int pad = ScreenshotBackdrop.Padding(shot.Size);

        // Every corner of the original is where it should be, at full size.
        int argb = SD.Color.FromArgb(255, 240, 0, 120).ToArgb();
        Assert.Equal(argb, framed.GetPixel(pad, pad).ToArgb());
        Assert.Equal(argb, framed.GetPixel(pad + shot.Width - 1, pad).ToArgb());
        Assert.Equal(argb, framed.GetPixel(pad, pad + shot.Height - 1).ToArgb());
        Assert.Equal(argb, framed.GetPixel(pad + shot.Width - 1, pad + shot.Height - 1).ToArgb());

        // And the margin is the backdrop, not more capture.
        Assert.NotEqual(argb, framed.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void SolidUsesTheColourThatWasTyped()
    {
        using SD.Bitmap shot = Shot();
        using SD.Bitmap framed = ScreenshotBackdrop.Compose(shot, "color", "#204080")!;
        // Sample the corner, which the drop shadow does not reach.
        Assert.Equal(SD.Color.FromArgb(255, 0x20, 0x40, 0x80).ToArgb(), framed.GetPixel(0, 0).ToArgb());
    }

    [Theory]
    [InlineData("#204080", 255, 0x20, 0x40, 0x80)]
    [InlineData("204080", 255, 0x20, 0x40, 0x80)]
    [InlineData("  #204080  ", 255, 0x20, 0x40, 0x80)]
    [InlineData("#80204080", 0x80, 0x20, 0x40, 0x80)]
    public void HexIsAcceptedWithOrWithoutAHashAndWithAlpha(string text, int a, int r, int g, int b)
    {
        Assert.Equal(SD.Color.FromArgb(a, r, g, b).ToArgb(), ScreenshotBackdrop.ParseColor(text).ToArgb());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#zzzzzz")]
    public void UnreadableColourFallsBackToTheAppSurface(string? text)
    {
        // Not an error mid-capture: the capture is still framed, in the window colour.
        Assert.Equal(ThemePalette.WindowBg.ToArgb(), ScreenshotBackdrop.ParseColor(text).ToArgb());
    }

    [Fact]
    public void PaddingHasAFloorSoSmallCropsStillGetAMargin()
    {
        Assert.Equal(24, ScreenshotBackdrop.Padding(new SD.Size(10, 10)));
        // And scales with the long edge for big ones.
        Assert.True(ScreenshotBackdrop.Padding(new SD.Size(3840, 2160)) > 100);
    }

    [Fact]
    public void ADegenerateCaptureIsRefusedRatherThanThrowing()
    {
        using var empty = new SD.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        // 1x1 is legal; what must not happen is an exception reaching the capture path.
        using SD.Bitmap? framed = ScreenshotBackdrop.Compose(empty, "gradient", null);
        Assert.NotNull(framed);
    }
}
