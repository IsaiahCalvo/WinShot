using System.Windows.Media.Imaging;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The brand is one mark from one file. These pin the shape of that contract: the shipped
/// .ico carries every size Windows asks for, and the tray icon is that asset rather than a
/// second mark drawn at runtime (which is how the app ended up with two different blues).
/// </summary>
public class AppBrandTests
{
    [Fact]
    public void AppIcon_ShipsEverySizeWindowsAsksFor()
    {
        using var stream = AppIcon.OpenStream();
        Assert.NotNull(stream);

        var decoder = new IconBitmapDecoder(
            stream!, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        int[] sizes = decoder.Frames.Select(f => f.PixelWidth).Order().ToArray();
        Assert.Equal([16, 24, 32, 48, 64, 128, 256], sizes);
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
    }

    [Fact]
    public void AppIcon_UsesTheSingleAccentBlue()
    {
        using SD.Bitmap? raster = AppIcon.Raster(256);
        Assert.NotNull(raster);

        // The squircle body: a point well inside the mark but clear of the white card and
        // the bracket. If the .ico is ever re-cut in another blue this fails loudly.
        SD.Color body = raster!.GetPixel(40, 210);
        Assert.Equal(ThemePalette.Accent.R, body.R);
        Assert.Equal(ThemePalette.Accent.G, body.G);
        Assert.Equal(ThemePalette.Accent.B, body.B);
    }

    [Fact]
    public void AppIcon_CornersAreTransparent_SoTheSquircleReadsAsAShape()
    {
        using SD.Bitmap? raster = AppIcon.Raster(256);
        Assert.NotNull(raster);
        Assert.Equal(0, raster!.GetPixel(1, 1).A);
        Assert.Equal(0, raster.GetPixel(254, 254).A);
    }

    [Fact]
    public void TrayIcon_IsTheShippedMark_NotADrawnCircle()
    {
        using SD.Bitmap tray = TrayIconFactory.Create().ToBitmap();
        using SD.Bitmap? asset = AppIcon.Raster(tray.Width);
        Assert.NotNull(asset);

        // The old factory filled an ellipse, so its corners were transparent AND its centre
        // was ringed white. Comparing against the asset at a handful of probe points is
        // enough to catch a regression back to a runtime-drawn second mark.
        foreach ((int x, int y) in new[] { (tray.Width / 8, tray.Height / 2), (tray.Width / 2, tray.Height / 4), (2, 2) })
        {
            SD.Color a = asset!.GetPixel(x, y);
            SD.Color b = tray.GetPixel(x, y);
            Assert.True(Math.Abs(a.A - b.A) <= 2 && Math.Abs(a.R - b.R) <= 2 &&
                        Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2,
                        $"tray pixel at {x},{y} does not match the shipped mark");
        }
    }

    [Fact]
    public void TrayIconVariants_AreDistinct_AndCachedPerVariant()
    {
        SD.Icon normal = TrayIconFactory.Create();
        SD.Icon slashed = TrayIconFactory.Create(TrayIconVariant.Slashed);
        SD.Icon confirm = TrayIconFactory.Create(TrayIconVariant.Confirm);

        Assert.NotSame(normal, slashed);
        Assert.NotSame(normal, confirm);
        // Cached: the confirmation animation must not allocate a GDI icon per frame.
        Assert.Same(normal, TrayIconFactory.Create());
        Assert.Same(slashed, TrayIconFactory.Create(TrayIconVariant.Slashed));

        using SD.Bitmap a = normal.ToBitmap();
        using SD.Bitmap b = slashed.ToBitmap();
        Assert.True(DifferingPixels(a, b) > 20, "the slashed variant must actually look different");
    }

    [Fact]
    public void ConfirmBadge_GrowsWithItsAnimationProgress()
    {
        using SD.Bitmap plain = TrayIconFactory.Create().ToBitmap();
        using SD.Bitmap small = TrayIconFactory.Create(TrayIconVariant.Confirm, 0.17).ToBitmap();
        using SD.Bitmap full = TrayIconFactory.Create(TrayIconVariant.Confirm, 1.0).ToBitmap();

        int atStart = DifferingPixels(plain, small);
        int atEnd = DifferingPixels(plain, full);
        Assert.True(atStart > 0, "the badge must be visible early in the animation");
        Assert.True(atEnd > atStart * 2, $"the badge must grow ({atStart} -> {atEnd})");
    }

    private static int DifferingPixels(SD.Bitmap a, SD.Bitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        int count = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }
}
