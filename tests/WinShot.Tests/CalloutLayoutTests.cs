using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class CalloutLayoutTests
{
    [Fact]
    public void FromDrag_PlacesBoxAtCursorAndKneesTowardIt()
    {
        var layout = CalloutLayout.FromDrag(new Point(10, 10), new Point(200, 80));
        Assert.Equal(200, layout.Box.X);
        Assert.Equal(80, layout.Box.Y);
        Assert.Equal(CalloutLayout.DefaultBoxWidth, layout.Box.Width);
        Assert.Equal(CalloutLayout.DefaultBoxHeight, layout.Box.Height);
        Assert.Equal(new Point(10, 10), layout.Tip);
        Assert.True((layout.Knee - layout.Tip).Length >= CalloutLayout.MinKneeToTip);
        Assert.True(layout.Box.Contains(layout.Join) || IsOnBorder(layout.Join, layout.Box));
    }

    [Fact]
    public void ClosestBorderPoint_OutsideClampsToEdge()
    {
        var box = new Rect(10, 10, 100, 40);
        Assert.Equal(new Point(10, 30), CalloutLayout.ClosestBorderPoint(new Point(0, 30), box));
        Assert.Equal(new Point(110, 30), CalloutLayout.ClosestBorderPoint(new Point(200, 30), box));
        Assert.Equal(new Point(60, 10), CalloutLayout.ClosestBorderPoint(new Point(60, 0), box));
        Assert.Equal(new Point(60, 50), CalloutLayout.ClosestBorderPoint(new Point(60, 90), box));
    }

    [Fact]
    public void ClosestBorderPoint_InsideSnapsToNearestEdge()
    {
        var box = new Rect(0, 0, 100, 40);
        Assert.Equal(new Point(0, 20), CalloutLayout.ClosestBorderPoint(new Point(5, 20), box));
        Assert.Equal(new Point(50, 0), CalloutLayout.ClosestBorderPoint(new Point(50, 2), box));
    }

    [Fact]
    public void WithTip_RebuildsJoinWithoutMovingBox()
    {
        var original = CalloutLayout.FromDrag(new Point(0, 0), new Point(100, 100));
        var moved = original.WithTip(new Point(20, 40));
        Assert.Equal(original.Box, moved.Box);
        Assert.Equal(new Point(20, 40), moved.Tip);
        Assert.True(IsOnBorder(moved.Join, moved.Box));
    }

    [Fact]
    public void Translate_MovesEveryHandleTogether()
    {
        var original = CalloutLayout.FromDrag(new Point(5, 5), new Point(80, 40));
        var moved = original.Translate(10, -4);
        Assert.Equal(original.Tip.X + 10, moved.Tip.X);
        Assert.Equal(original.Tip.Y - 4, moved.Tip.Y);
        Assert.Equal(original.Box.X + 10, moved.Box.X);
        Assert.Equal(original.Box.Y - 4, moved.Box.Y);
        Assert.Equal(original.Box.Width, moved.Box.Width);
    }

    [Fact]
    public void FromParts_EnforcesMinimumBoxSize()
    {
        var layout = CalloutLayout.FromParts(
            new Point(0, 0), new Point(20, 20), new Rect(40, 40, 10, 5));
        Assert.True(layout.Box.Width >= CalloutLayout.MinBoxWidth);
        Assert.True(layout.Box.Height >= CalloutLayout.MinBoxHeight);
    }

    [Fact]
    public void Bounds_ContainsTipKneeAndBox()
    {
        var layout = CalloutLayout.FromDrag(new Point(0, 0), new Point(100, 50));
        var b = layout.Bounds();
        Assert.True(b.Contains(layout.Tip) || IsOnBorder(layout.Tip, b));
        Assert.True(b.Contains(layout.Knee) || IsOnBorder(layout.Knee, b));
        Assert.True(b.Contains(layout.Box.TopLeft) || IsOnBorder(layout.Box.TopLeft, b));
        Assert.True(b.Contains(layout.Box.BottomRight) || IsOnBorder(layout.Box.BottomRight, b));
    }

    private static bool IsOnBorder(Point p, Rect box, double eps = 0.51)
    {
        bool onX = Math.Abs(p.X - box.Left) <= eps || Math.Abs(p.X - box.Right) <= eps;
        bool onY = Math.Abs(p.Y - box.Top) <= eps || Math.Abs(p.Y - box.Bottom) <= eps;
        bool inX = p.X >= box.Left - eps && p.X <= box.Right + eps;
        bool inY = p.Y >= box.Top - eps && p.Y <= box.Bottom + eps;
        return (onX && inY) || (onY && inX);
    }
}
