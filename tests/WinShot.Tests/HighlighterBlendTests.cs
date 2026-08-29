using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class HighlighterBlendTests
{
    [Fact]
    public void MultiplyChannel_OpaqueYellowOnWhiteStaysYellowish()
    {
        // White * yellow = yellow. Full alpha.
        Assert.Equal(255, HighlighterBlend.MultiplyChannel(255, 255, 255));
        Assert.Equal(235, HighlighterBlend.MultiplyChannel(255, 235, 255));
        Assert.Equal(59, HighlighterBlend.MultiplyChannel(255, 59, 255));
    }

    [Fact]
    public void MultiplyChannel_HalfAlphaYellowOnBlueTintsWithoutWashingToWhite()
    {
        byte live = HighlighterBlend.MultiplyChannel(backdrop: 200, source: 255, sourceAlpha: 128);
        // src-over yellow would be ~227; multiply is darker.
        Assert.InRange(live, 200, 230);
        Assert.True(live < 227);
    }

    [Fact]
    public void MultiplyOnto_OpaqueRedOnWhiteBecomesRed()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 255, 255, 255));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(255, 255, 0, 0));
        HighlighterBlend.MultiplyOnto(dest, overlay);
        var c = dest.GetPixel(0, 0);
        Assert.Equal(255, c.R);
        Assert.Equal(0, c.G);
        Assert.Equal(0, c.B);
    }

    [Fact]
    public void MultiplyOnto_HalfYellowDarkensBlueBackdrop()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 0, 0, 200));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(128, 255, 235, 59));
        HighlighterBlend.MultiplyOnto(dest, overlay);
        var c = dest.GetPixel(0, 0);
        byte expectedB = HighlighterBlend.MultiplyChannel(200, 59, 128);
        Assert.Equal(expectedB, c.B);
        Assert.True(c.B < 200, $"blue channel should darken, got {c}");
    }

    [Fact]
    public void SrcOver_TransparentOverlayLeavesDest()
    {
        using var dest = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        using var overlay = new SD.Bitmap(1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        dest.SetPixel(0, 0, SD.Color.FromArgb(255, 10, 20, 30));
        overlay.SetPixel(0, 0, SD.Color.FromArgb(0, 255, 0, 0));
        HighlighterBlend.SrcOver(dest, overlay);
        Assert.Equal(SD.Color.FromArgb(255, 10, 20, 30), dest.GetPixel(0, 0));
    }
}
