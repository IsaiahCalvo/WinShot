using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Clicking one edge of a framed selection to snap just that edge, and padding the whole
/// selection out a step. The handle model only ever offered "drag this corner and eyeball the
/// last three pixels".
/// </summary>
public class MarqueeEdgeTests
{
    private static readonly SD.Rectangle Region = new(100, 200, 400, 300); // right 500, bottom 500
    private static readonly SD.Rectangle Desktop = new(0, 0, 1920, 1080);
    private static readonly int[] EdgesX = [40, 96, 520, 900];
    private static readonly int[] EdgesY = [180, 205, 496, 700];

    [Theory]
    [InlineData(100, 350, MarqueeSide.Left)]
    [InlineData(103, 350, MarqueeSide.Left)]
    [InlineData(500, 350, MarqueeSide.Right)]
    [InlineData(300, 200, MarqueeSide.Top)]
    [InlineData(300, 500, MarqueeSide.Bottom)]
    [InlineData(300, 350, MarqueeSide.None)]  // the middle belongs to the move gesture
    [InlineData(700, 350, MarqueeSide.None)]  // nowhere near
    public void HitTestNamesTheSideUnderThePointer(int x, int y, MarqueeSide expected)
    {
        Assert.Equal(expected, MarqueeEdge.HitTest(Region, new SD.Point(x, y)));
    }

    [Theory]
    [InlineData(100, 200)]  // top-left
    [InlineData(500, 200)]  // top-right
    [InlineData(100, 500)]  // bottom-left
    [InlineData(500, 500)]  // bottom-right
    public void CornersBelongToTheResizeHandlesNotToTheEdgeGesture(int x, int y)
    {
        // Without this the edge gesture would swallow every corner grab inside its tolerance.
        Assert.Equal(MarqueeSide.None, MarqueeEdge.HitTest(Region, new SD.Point(x, y)));
    }

    [Fact]
    public void SnappingOneEdgeLeavesTheOtherThreeAlone()
    {
        SD.Rectangle snapped = MarqueeEdge.SnapEdge(Region, MarqueeSide.Left, EdgesX, EdgesY);

        Assert.Equal(96, snapped.Left);
        Assert.Equal(500, snapped.Right);
        Assert.Equal(200, snapped.Top);
        Assert.Equal(500, snapped.Bottom);
    }

    [Fact]
    public void EachSideSnapsToItsOwnAxis()
    {
        Assert.Equal(520, MarqueeEdge.SnapEdge(Region, MarqueeSide.Right, EdgesX, EdgesY).Right);
        Assert.Equal(205, MarqueeEdge.SnapEdge(Region, MarqueeSide.Top, EdgesX, EdgesY).Top);
        Assert.Equal(496, MarqueeEdge.SnapEdge(Region, MarqueeSide.Bottom, EdgesX, EdgesY).Bottom);
    }

    [Fact]
    public void AnEdgeWithNothingInReachStaysWhereItIs()
    {
        Assert.Equal(Region, MarqueeEdge.SnapEdge(Region, MarqueeSide.Left, [], [], reach: 48));
        Assert.Equal(Region, MarqueeEdge.SnapEdge(Region, MarqueeSide.Left, [1000], EdgesY, reach: 48));
        Assert.Equal(Region, MarqueeEdge.SnapEdge(Region, MarqueeSide.None, EdgesX, EdgesY));
    }

    [Fact]
    public void SnappingNeverCollapsesTheSelection()
    {
        // A boundary past the opposite edge would invert the rectangle; the snap is refused.
        Assert.Equal(Region, MarqueeEdge.SnapEdge(Region, MarqueeSide.Left, [520], EdgesY, reach: 500));
    }

    [Fact]
    public void PaddingGrowsEverySideAndStopsAtTheDesktopEdge()
    {
        Assert.Equal(new SD.Rectangle(92, 192, 416, 316), MarqueeEdge.Pad(Region, 8, Desktop));

        var atEdge = new SD.Rectangle(0, 0, 100, 100);
        SD.Rectangle padded = MarqueeEdge.Pad(atEdge, 8, Desktop);
        Assert.Equal(0, padded.Left);
        Assert.Equal(0, padded.Top);
        Assert.Equal(108, padded.Right);
    }

    [Fact]
    public void PaddingWorksOnADesktopThatStartsAtNegativeCoordinates()
    {
        var desktop = new SD.Rectangle(-1920, -1080, 3840, 2160);
        SD.Rectangle padded = MarqueeEdge.Pad(new SD.Rectangle(-1916, -1076, 100, 100), 8, desktop);

        Assert.Equal(-1920, padded.Left);
        Assert.Equal(-1080, padded.Top);
        Assert.True(padded.Width >= 100);
    }

    [Fact]
    public void SnapAllLinesUpEveryEdgeThatHasSomethingInReach()
    {
        SD.Rectangle snapped = MarqueeEdge.SnapAll(Region, EdgesX, EdgesY);

        Assert.Equal(96, snapped.Left);
        Assert.Equal(520, snapped.Right);
        Assert.Equal(205, snapped.Top);
        Assert.Equal(496, snapped.Bottom);
    }

    [Fact]
    public void SnapAllLeavesEdgesWithNothingNearThemExactlyWhereTheyWere()
    {
        // Only the left edge has a boundary within reach; the other three are untouched.
        SD.Rectangle snapped = MarqueeEdge.SnapAll(Region, [96], []);

        Assert.Equal(96, snapped.Left);
        Assert.Equal(500, snapped.Right);
        Assert.Equal(200, snapped.Top);
        Assert.Equal(500, snapped.Bottom);
    }

    [Fact]
    public void SnapAllOfAnAlreadyAlignedSelectionChangesNothing()
    {
        var aligned = new SD.Rectangle(96, 205, 424, 291); // left 96, right 520, top 205, bottom 496

        Assert.Equal(aligned, MarqueeEdge.SnapAll(aligned, EdgesX, EdgesY));
    }
}
