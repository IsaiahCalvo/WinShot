using System.Linq;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The border-only region marker shown when dimming is off. The load-bearing property is the
/// same one the dim bands have: nothing it draws may land on a recorded pixel.
/// </summary>
public class RecordingRegionMarkerTests
{
    private static readonly SD.Rectangle Desktop = new(0, 0, 1920, 1080);

    /// <summary>Rectangle.Intersect returns a zero-AREA rectangle at a non-zero position for
    /// merely adjacent rectangles, and Rectangle.IsEmpty only reports the all-zero one — so
    /// "they do not overlap" has to be asked as "the overlap has no area".</summary>
    private static bool NoOverlap(SD.Rectangle a, SD.Rectangle b)
    {
        SD.Rectangle shared = SD.Rectangle.Intersect(a, b);
        return shared.Width <= 0 || shared.Height <= 0;
    }

    [Fact]
    public void Strips_NeverOverlapTheRecordedArea()
    {
        var region = new SD.Rectangle(300, 200, 800, 500);

        var strips = RecordingRegionMarkerLayout.Strips(Desktop, region, thickness: 10);

        Assert.NotEmpty(strips);
        Assert.All(strips, s => Assert.True(NoOverlap(s.Bounds, region)));
    }

    [Fact]
    public void Strips_HugAllFourEdges()
    {
        var region = new SD.Rectangle(300, 200, 800, 500);

        var strips = RecordingRegionMarkerLayout.Strips(Desktop, region, thickness: 10);

        Assert.Equal(4, strips.Count);
        Assert.Equal(region.Top, strips.Single(s => s.Edge == RecordingRegionMarkerEdge.Top).Bounds.Bottom);
        Assert.Equal(region.Bottom, strips.Single(s => s.Edge == RecordingRegionMarkerEdge.Bottom).Bounds.Top);
        Assert.Equal(region.Left, strips.Single(s => s.Edge == RecordingRegionMarkerEdge.Left).Bounds.Right);
        Assert.Equal(region.Right, strips.Single(s => s.Edge == RecordingRegionMarkerEdge.Right).Bounds.Left);
    }

    [Fact]
    public void Strips_AreClippedToTheDesktopAndDroppedWhenOffScreen()
    {
        // A region flush against the top-left corner has no room above or to the left of it.
        var region = new SD.Rectangle(0, 0, 400, 300);

        var strips = RecordingRegionMarkerLayout.Strips(Desktop, region, thickness: 10);

        Assert.DoesNotContain(strips, s => s.Edge == RecordingRegionMarkerEdge.Top);
        Assert.DoesNotContain(strips, s => s.Edge == RecordingRegionMarkerEdge.Left);
        Assert.All(strips, s => Assert.True(Desktop.Contains(s.Bounds)));
    }

    [Fact]
    public void Strips_WorkOnANegativeYDesktop()
    {
        // Isaiah's monitors sit above the laptop, so the virtual desktop starts negative.
        var desktop = new SD.Rectangle(-1920, -1080, 3840, 2160);
        var region = new SD.Rectangle(-1500, -900, 600, 400);

        var strips = RecordingRegionMarkerLayout.Strips(desktop, region, thickness: 8);

        Assert.Equal(4, strips.Count);
        Assert.All(strips, s => Assert.True(NoOverlap(s.Bounds, region)));
        Assert.All(strips, s => Assert.True(desktop.Contains(s.Bounds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Strips_AreEmptyForANonPositiveThickness(int thickness) =>
        Assert.Empty(RecordingRegionMarkerLayout.Strips(Desktop, new SD.Rectangle(10, 10, 100, 100), thickness));

    [Fact]
    public void Strips_AreEmptyForADegenerateRegion() =>
        Assert.Empty(RecordingRegionMarkerLayout.Strips(Desktop, new SD.Rectangle(10, 10, 0, 0), thickness: 10));

    /// <summary>The left and right strips run past the corners so each corner is painted by
    /// exactly one strip rather than twice (which would double the glow there).</summary>
    [Fact]
    public void Strips_PaintEachCornerExactlyOnce()
    {
        var region = new SD.Rectangle(300, 200, 800, 500);

        var strips = RecordingRegionMarkerLayout.Strips(Desktop, region, thickness: 10);

        foreach ((RecordingRegionMarkerEdge Edge, SD.Rectangle Bounds) a in strips)
        {
            foreach ((RecordingRegionMarkerEdge Edge, SD.Rectangle Bounds) b in strips)
            {
                if (a.Edge == b.Edge)
                    continue;
                Assert.True(NoOverlap(a.Bounds, b.Bounds));
            }
        }
    }
}
