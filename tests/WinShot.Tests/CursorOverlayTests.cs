using WinShot.Core;
using WinShot.SettingsUi;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Drawing the pointer into a capture. The screen-capture APIs never include it, so
/// "Show cursor on screenshots" was a stored setting that did nothing.
/// </summary>
public class CursorOverlayTests
{
    private static SD.Bitmap Solid(int size, SD.Color color)
    {
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    [Fact]
    public void ACursorOutsideTheCapturedAreaLeavesEveryPixelAlone()
    {
        using SD.Bitmap capture = Solid(8, SD.Color.Red);

        // The captured area is nowhere near the pointer, so the composite is a no-op —
        // which is also what happens on a desktop with no visible cursor at all.
        CursorOverlay.Draw(capture, new SD.Rectangle(500_000, 500_000, 8, 8));

        for (int y = 0; y < capture.Height; y++)
        {
            for (int x = 0; x < capture.Width; x++)
                Assert.Equal(SD.Color.Red.ToArgb(), capture.GetPixel(x, y).ToArgb());
        }
    }

    [Fact]
    public void DrawingIsBestEffortAndNeverCostsTheUserTheirCapture()
    {
        using SD.Bitmap capture = Solid(4, SD.Color.Blue);

        // Repeated over a rect that straddles the desktop origin: whatever the cursor is
        // doing on the hidden test desktop, none of it may throw out of a capture flow.
        for (int i = 0; i < 5; i++)
            CursorOverlay.Draw(capture, new SD.Rectangle(-2, -2, 4, 4));

        Assert.Equal(4, capture.Width);
    }

    [Fact]
    public void TheSettingIsOfferedAsAWorkingOptionNowThatItDoesSomething()
    {
        Assert.Contains("CursorOnScreenshots", SettingsTruthCatalog.VisibleRuntimeBackedProperties);
        Assert.DoesNotContain("CursorOnScreenshots", SettingsTruthCatalog.StoredButUnavailableProperties);
        Assert.False(new Settings().CursorOnScreenshots);
    }
}
