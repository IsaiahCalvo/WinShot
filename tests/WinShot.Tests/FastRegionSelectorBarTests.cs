using System.Reflection;
using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Verifies the Cancel/Done bar placement for a region selection: below the region by default,
/// flipped above when it would run off the monitor bottom, and tucked inside the region's bottom
/// when the region spans the full screen height. (BarGap=12, BarPad=8 inside the selector.)
/// </summary>
public class FastRegionSelectorBarTests
{
    private const int BarW = 180, BarH = 46; // BarH = BarPad(8) + BarBtnH(30) + BarPad(8)
    private static readonly SD.Rectangle Monitor = new(0, 0, 1920, 1080);

    private static SD.Point Place(SD.Rectangle region)
    {
        var mi = typeof(FastRegionSelectorDialog).GetMethod(
            "PlaceActionBar", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (SD.Point)mi.Invoke(null, new object[] { region, Monitor, BarW, BarH })!;
    }

    [Fact]
    public void Default_PlacesBelowRegion_CenteredHorizontally()
    {
        var r = new SD.Rectangle(800, 400, 300, 200); // bottom = 600, room below
        var p = Place(r);
        Assert.Equal(600 + 12, p.Y);                 // just below the region (BarGap)
        Assert.Equal(800 + (300 - BarW) / 2, p.X);   // centered on the region
    }

    [Fact]
    public void RegionAtScreenBottom_FlipsAbove()
    {
        var r = new SD.Rectangle(800, 1000, 300, 80); // bottom = 1080 (screen edge): no room below
        var p = Place(r);
        Assert.Equal(1000 - BarH - 12, p.Y);          // above the region
    }

    [Fact]
    public void FullHeightRegion_TucksInsideBottom()
    {
        var r = new SD.Rectangle(800, 0, 300, 1080);  // spans the whole height: no room above OR below
        var p = Place(r);
        Assert.Equal(1080 - BarH - 12, p.Y);          // inside the region, near the bottom
        Assert.True(p.Y > 1080 / 2, "bar should sit in the lower half, not centered");
    }

    [Fact]
    public void NarrowRegion_ClampsBarOnMonitor()
    {
        var r = new SD.Rectangle(10, 400, 40, 100);   // bar wider than region, near left edge
        var p = Place(r);
        Assert.True(p.X >= 8, $"bar pushed off the left edge (x={p.X})");
        Assert.True(p.X + BarW <= 1920 - 8, $"bar pushed off the right edge (x={p.X})");
    }
}
