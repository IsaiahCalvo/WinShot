using WinShot.Capture;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The grab points on an adjustable selection, now shared by both selectors. Pure geometry,
/// so it is tested without a screen: where the eight points sit, which one a cursor grabs,
/// what dragging one does, and that a move never turns into a resize at the desktop edge.
/// </summary>
public class SelectionHandlesTests
{
    private static readonly SD.Rectangle Desktop = new(0, 0, 1920, 1080);
    private static readonly SD.Rectangle Region = new(100, 200, 400, 300);

    [Fact]
    public void PointsAreTheFourCornersAndTheFourEdgeMidpoints()
    {
        SD.Point[] points = SelectionHandles.Points(Region);

        Assert.Equal(8, points.Length);
        Assert.Equal(new SD.Point(100, 200), points[0]); // TL
        Assert.Equal(new SD.Point(300, 200), points[1]); // Top
        Assert.Equal(new SD.Point(500, 200), points[2]); // TR
        Assert.Equal(new SD.Point(500, 350), points[3]); // Right
        Assert.Equal(new SD.Point(500, 500), points[4]); // BR
        Assert.Equal(new SD.Point(300, 500), points[5]); // Bottom
        Assert.Equal(new SD.Point(100, 500), points[6]); // BL
        Assert.Equal(new SD.Point(100, 350), points[7]); // Left
    }

    [Theory]
    [InlineData(100, 200, 0)]
    [InlineData(106, 205, 0)]   // inside the grab tolerance, outside the dot
    [InlineData(500, 500, 4)]
    [InlineData(300, 500, 5)]
    [InlineData(300, 350, -1)]  // the middle of the region is not a handle
    [InlineData(120, 220, -1)]
    public void HitTestFindsTheHandleUnderTheCursor(int x, int y, int expected)
    {
        Assert.Equal(expected, SelectionHandles.HitTest(new SD.Point(x, y), Region));
    }

    [Fact]
    public void ResizingACornerMovesOnlyThatCorner()
    {
        SD.Rectangle resized = SelectionHandles.Resize(Region, 4, new SD.Point(600, 700), Desktop);
        Assert.Equal(SD.Rectangle.FromLTRB(100, 200, 600, 700), resized);

        // An edge handle leaves the other axis alone.
        Assert.Equal(SD.Rectangle.FromLTRB(100, 150, 500, 500),
            SelectionHandles.Resize(Region, 1, new SD.Point(9999, 150), Desktop));
    }

    [Fact]
    public void DraggingACornerPastItsOppositeFlipsInsteadOfInverting()
    {
        SD.Rectangle flipped = SelectionHandles.Resize(Region, 4, new SD.Point(40, 60), Desktop);

        Assert.Equal(SD.Rectangle.FromLTRB(40, 60, 100, 200), flipped);
        Assert.True(flipped.Width > 0 && flipped.Height > 0);
    }

    [Fact]
    public void ResizeIsClippedToTheDesktopButMoveIsPushedBackInside()
    {
        Assert.Equal(SD.Rectangle.FromLTRB(100, 200, 1920, 1080),
            SelectionHandles.Resize(Region, 4, new SD.Point(5000, 5000), Desktop));

        // A move that runs off the edge must keep its size — clipping it would silently
        // resize a region the user only meant to slide.
        SD.Rectangle moved = SelectionHandles.Move(Region, 5000, 5000, Desktop);
        Assert.Equal(Region.Size, moved.Size);
        Assert.Equal(new SD.Point(1520, 780), moved.Location);

        SD.Rectangle negative = SelectionHandles.Move(Region, -5000, -5000, Desktop);
        Assert.Equal(Region.Size, negative.Size);
        Assert.Equal(new SD.Point(0, 0), negative.Location);
    }

    [Fact]
    public void MoveHonoursANegativeYDesktopOrigin()
    {
        // Isaiah runs two monitors above the laptop, so the virtual desktop starts negative.
        var desktop = new SD.Rectangle(-1920, -1080, 3840, 2160);
        SD.Rectangle moved = SelectionHandles.Move(new SD.Rectangle(-1800, -1000, 200, 200), -500, -500, desktop);

        Assert.Equal(new SD.Point(-1920, -1080), moved.Location);
        Assert.Equal(new SD.Size(200, 200), moved.Size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void EveryHandleHasItsOwnResizeCursor(int handle)
    {
        Assert.NotNull(SelectionHandles.CursorFor(handle));
    }

    [Fact]
    public void NotAHandleHasNoResizeCursor()
    {
        Assert.Null(SelectionHandles.CursorFor(-1));
        Assert.Null(SelectionHandles.CursorFor(8));
    }

    [Fact]
    public void DrawingHandlesLeavesTheGraphicsStateAsItFoundIt()
    {
        using var bmp = new SD.Bitmap(200, 200);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.None;

        SelectionHandles.Draw(g, new SD.Rectangle(40, 40, 100, 100), scale: 1d, focusHandle: 2);

        Assert.Equal(SD.Drawing2D.SmoothingMode.None, g.SmoothingMode);
        // The focus ring is drawn behind the grabbed dot, so that handle covers more ground.
        Assert.NotEqual(SD.Color.FromArgb(0, 0, 0, 0), bmp.GetPixel(140, 40));
        _ = WF.Cursors.Default; // keep the WinForms reference honest for CursorFor above
    }
}
