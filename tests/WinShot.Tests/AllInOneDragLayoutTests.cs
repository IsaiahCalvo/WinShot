using System.Drawing;
using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class AllInOneDragLayoutTests
{
    [Fact]
    public void CreatePixelRectangle_NormalizesFreeformDrag()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(10, 20),
            new SD.Point(4, 8),
            aspectRatio: null);

        Assert.Equal(new Rectangle(4, 8, 6, 12), rect);
    }

    [Fact]
    public void CreatePixelRectangle_ExtendsHeightWhenAspectLocked()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(0, 0),
            new SD.Point(160, 10),
            aspectRatio: 16d / 9d);

        Assert.Equal(new Rectangle(0, 0, 160, 90), rect);
    }

    [Fact]
    public void CreatePixelRectangle_PreservesDragDirectionWhenAspectLocked()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(10, 10),
            new SD.Point(4, 2),
            aspectRatio: 1);

        Assert.Equal(new Rectangle(2, 2, 8, 8), rect);
    }


    [Theory]
    [InlineData(300, 120, 300, 300)]
    [InlineData(120, 300, 300, 300)]
    [InlineData(-300, 120, 300, 300)]
    [InlineData(-120, -300, 300, 300)]
    public void CreatePixelRectangle_SquaresTheDragWhenTheRatioIsOne(int dx, int dy, int width, int height)
    {
        // Shift-to-square in the region selector is the 1:1 case of this same constraint —
        // the selector reads the modifier and hands the ratio in rather than owning the math.
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(500, 500),
            new SD.Point(500 + dx, 500 + dy),
            aspectRatio: 1d);

        Assert.Equal(width, rect.Width);
        Assert.Equal(height, rect.Height);
        Assert.Equal(rect.Width, rect.Height);
    }

    [Fact]
    public void CreatePixelRectangle_SquareGrowsInTheDirectionTheDragWentAndKeepsItsAnchor()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(500, 500), new SD.Point(200, 620), aspectRatio: 1d);

        // Dragging up-left from the anchor: the square hangs off the anchor's corner, not
        // through it.
        Assert.Equal(200, rect.Left);
        Assert.Equal(500, rect.Right);
        Assert.Equal(500, rect.Top);
        Assert.Equal(800, rect.Bottom);
    }
}
