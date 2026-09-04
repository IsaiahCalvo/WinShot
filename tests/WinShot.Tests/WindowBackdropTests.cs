using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Compositing a captured window onto its backdrop. The styled result used to be reachable
/// only through one unbound tray item that opened the whole background composer; a plain
/// window capture was a bare rectangle.
/// </summary>
public class WindowBackdropTests
{
    private static SD.Bitmap Solid(int width, int height, SD.Color color)
    {
        var bmp = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    [Theory]
    [InlineData("min", 24)]
    [InlineData("default", 48)]
    [InlineData("max", 96)]
    [InlineData("", 48)]
    [InlineData(null, 48)]
    [InlineData("nonsense", 48)]
    public void PaddingPresetsMapToPixels(string? preset, int expected)
    {
        Assert.Equal(expected, WindowBackdrop.PaddingFor(preset));
    }

    [Theory]
    [InlineData("wallpaper", true)]
    [InlineData(null, true)]      // an older settings file has no value; composite is the default
    [InlineData("none", false)]
    [InlineData("None", false)]
    public void OnlyNoneTurnsTheBackdropOff(string? mode, bool enabled)
    {
        Assert.Equal(enabled, WindowBackdrop.IsEnabled(mode));
    }

    [Fact]
    public void TheCanvasGrowsByThePaddingOnEverySide()
    {
        using SD.Bitmap window = Solid(200, 120, SD.Color.Red);
        using SD.Bitmap backdrop = Solid(64, 64, SD.Color.Lime);

        using SD.Bitmap composed = WindowBackdrop.Compose(window, backdrop, padding: 40);

        Assert.Equal(280, composed.Width);
        Assert.Equal(200, composed.Height);
    }

    [Fact]
    public void TheWindowLandsCentredWithTheBackdropShowingAroundIt()
    {
        using SD.Bitmap window = Solid(200, 120, SD.Color.Red);
        using SD.Bitmap backdrop = Solid(8, 8, SD.Color.Lime);

        using SD.Bitmap composed = WindowBackdrop.Compose(window, backdrop, padding: 40);

        Assert.Equal(SD.Color.Red.ToArgb(), composed.GetPixel(140, 100).ToArgb());
        // Backdrop, not black: the padding band would be empty if the wallpaper had not been
        // drawn. Sampled a few px in, where the resampler is not blending with the canvas edge.
        SD.Color band = composed.GetPixel(8, 8);
        Assert.True(band.G > band.R && band.G > band.B, $"expected the wallpaper in the padding band, got {band}");
    }

    [Fact]
    public void TheCornersAreRoundedSoTheWindowReadsAsAnObject()
    {
        using SD.Bitmap window = Solid(400, 300, SD.Color.Red);
        using SD.Bitmap backdrop = Solid(8, 8, SD.Color.Lime);

        using SD.Bitmap composed = WindowBackdrop.Compose(window, backdrop, padding: 40);

        // Dead-on the window's top-left corner pixel the rounding leaves the backdrop
        // (darkened by the shadow) rather than the window's own colour.
        Assert.NotEqual(SD.Color.Red.ToArgb(), composed.GetPixel(40, 40).ToArgb());
        Assert.Equal(SD.Color.Red.ToArgb(), composed.GetPixel(240, 190).ToArgb());
    }

    [Fact]
    public void NoWallpaperStillProducesAMockupRatherThanABlackFrame()
    {
        using SD.Bitmap window = Solid(120, 80, SD.Color.Red);

        using SD.Bitmap composed = WindowBackdrop.Compose(window, backdrop: null, padding: 24);

        SD.Color corner = composed.GetPixel(1, 1);
        Assert.Equal(255, corner.A);
        Assert.True(corner.R + corner.G + corner.B > 0, "the fallback gradient should not be pure black");
    }

    [Fact]
    public void ZeroPaddingIsAllowedAndLeavesTheWindowItsOwnSize()
    {
        using SD.Bitmap window = Solid(64, 48, SD.Color.Red);

        using SD.Bitmap composed = WindowBackdrop.Compose(window, backdrop: null, padding: 0);

        Assert.Equal(64, composed.Width);
        Assert.Equal(48, composed.Height);
    }
}
