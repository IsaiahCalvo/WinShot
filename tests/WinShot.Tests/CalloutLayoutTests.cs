using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class CalloutLayoutTests
{
    [Fact]
    public void FromDrag_PlacesBoxAtCursorAndOrbitsTheKneeAroundTheTip()
    {
        // Survey (SVGAnnotationLayer.jsx:1122-1134): the knee sits CreateKneeLength out from
        // the tip along tip -> text-box centre, so it swivels with the box and the committed
        // geometry is pixel-identical to the creation ghost.
        var layout = CalloutLayout.FromDrag(new Point(10, 10), new Point(200, 80));
        Assert.Equal(200, layout.Box.X);
        Assert.Equal(80, layout.Box.Y);
        Assert.Equal(CalloutLayout.DefaultBoxWidth, layout.Box.Width);
        Assert.Equal(CalloutLayout.DefaultBoxHeight, layout.Box.Height);
        Assert.Equal(new Point(10, 10), layout.Tip);
        Assert.Equal(CalloutLayout.CreateKneeLength, (layout.Knee - layout.Tip).Length, 3);
        var toCentre = new Vector(
            layout.Box.X + layout.Box.Width / 2 - layout.Tip.X,
            layout.Box.Y + layout.Box.Height / 2 - layout.Tip.Y);
        toCentre.Normalize();
        Assert.Equal(layout.Tip + toCentre * CalloutLayout.CreateKneeLength, layout.Knee);
        Assert.True(layout.Box.Contains(layout.Join) || IsOnBorder(layout.Join, layout.Box));
    }

    [Fact]
    public void DefaultKnee_IsFortyPixelsFromTheTipTowardTheBoxCentre()
    {
        var box = new Rect(100, 0, 120, 32);
        var knee = CalloutLayout.DefaultKnee(new Point(0, 0), box);
        Assert.Equal(CalloutLayout.CreateKneeLength, (knee - new Point(0, 0)).Length, 6);
    }

    [Fact]
    public void PushBoxOffTip_ShovesTheBoxOutOfTheTipsForbiddenZone()
    {
        // Survey SVGAnnotationLayer.jsx:1085-1104 - a (120 + 10) x (32 + 10) rect around the
        // tip that the pointer-driven box origin can slide along but never enter.
        var tip = new Point(100, 100);
        Assert.Equal(new Point(110, 95), CalloutLayout.PushBoxOffTip(tip, new Point(95, 95)));
        // Outside the zone the origin is untouched, and the boundary itself is legal.
        Assert.Equal(new Point(300, 300), CalloutLayout.PushBoxOffTip(tip, new Point(300, 300)));
        Assert.Equal(new Point(110, 95), CalloutLayout.PushBoxOffTip(tip, new Point(110, 95)));
    }

    [Fact]
    public void FromParts_ReRoutesOffTheArrowSideWhenTheLeaderWouldCrossTheBox()
    {
        // Survey calloutGeometry.js:455-537 - the arrow-side border point keeps line1 on the
        // arrow's side; the knee-nearest edge is what forces the leader back through the box.
        var box = new Rect(100, 100, 100, 40);
        var layout = CalloutLayout.FromParts(new Point(50, 120), new Point(250, 120), box);
        Assert.Equal(new Point(100, 120), layout.Join);
        Assert.Equal(new Point(75, 120), layout.Knee);
        Assert.False(CalloutLayout.SegmentCrossesRect(layout.Tip, layout.Knee, layout.Box));
    }

    [Fact]
    public void FromParts_LeavesAKneeJustOutsideTheBorderWhereTheUserPutIt()
    {
        // Survey 2026-05-18 (calloutGeometry.js:547-560): re-routing on mere proximity made
        // the knee handle jump on the next grab, so only a knee GENUINELY inside is moved.
        var box = new Rect(100, 100, 100, 40);
        var layout = CalloutLayout.FromParts(new Point(50, 300), new Point(97, 120), box);
        Assert.Equal(new Point(97, 120), layout.Knee);
    }

    [Fact]
    public void IsDropSafe_MirrorsSurveysReleaseTimeRules()
    {
        var box = new Rect(100, 100, 100, 40);
        Assert.True(CalloutLayout.IsDropSafe(
            CalloutLayout.Raw(new Point(20, 120), new Point(60, 120), box)));
        // Arrow tip inside the box.
        Assert.False(CalloutLayout.IsDropSafe(
            CalloutLayout.Raw(new Point(150, 120), new Point(60, 120), box)));
        // Knee stacked on a box corner handle.
        Assert.False(CalloutLayout.IsDropSafe(
            CalloutLayout.Raw(new Point(20, 120), new Point(96, 96), box)));
        // Leader cutting straight through the box.
        Assert.False(CalloutLayout.IsDropSafe(
            CalloutLayout.Raw(new Point(20, 120), new Point(260, 120), box)));
    }

    [Fact]
    public void RenderBox_AddsTheDescenderBufferWithoutTouchingStoredHeight()
    {
        double buffer = CalloutLayout.DefaultFontSize * CalloutLayout.DescenderRatio;
        var layout = CalloutLayout.FromParts(
            new Point(0, 0), new Point(40, 40), new Rect(100, 100, 120, 32), buffer);
        Assert.Equal(32, layout.Box.Height);
        Assert.Equal(32 + buffer, layout.RenderBox.Height, 6);
        Assert.Equal(layout.Box.Width, layout.RenderBox.Width);
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
    public void FromParts_PushesKneeOutsideTheBox()
    {
        var layout = CalloutLayout.FromParts(
            new Point(0, 20), new Point(70, 30), new Rect(40, 10, 80, 40));
        Assert.False(layout.Box.Contains(layout.Knee));
        Assert.True(CalloutLayout.DistanceToBoxEdge(layout.Knee, layout.Box) >= CalloutLayout.MinKneeToBoxEdge - 0.01);
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
