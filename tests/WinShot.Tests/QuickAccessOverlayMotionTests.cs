using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class QuickAccessOverlayMotionTests
{
    [Theory]
    [InlineData("bottom-right", true)]
    [InlineData("right", true)]
    [InlineData("bottom-left", false)]
    [InlineData("left", false)]
    public void ExitTargetMovesCompletelyPastTheNearestHorizontalEdge(string position, bool exitsRight)
    {
        var workingArea = new SD.Rectangle(-1920, -120, 1920, 1040);
        var bounds = new SD.Rectangle(-220, 780, 192, 120);

        SD.Point target = QuickAccessOverlayMotion.ExitTarget(workingArea, bounds, position);

        Assert.Equal(bounds.Top, target.Y);
        if (exitsRight)
            Assert.True(target.X > workingArea.Right);
        else
            Assert.True(target.X + bounds.Width < workingArea.Left);
    }

    [Fact]
    public void BottomRightExitStartsAndFinishesGentlyWhileFading()
    {
        var start = new SD.Point(1700, 900);
        var end = new SD.Point(1952, 900);
        double early = QuickAccessOverlayMotion.EaseInOutSine(0.25);
        double middle = QuickAccessOverlayMotion.EaseInOutSine(0.5);
        double late = QuickAccessOverlayMotion.EaseInOutSine(0.75);

        SD.Point earlyPoint = QuickAccessOverlayMotion.Interpolate(start, end, early);
        SD.Point latePoint = QuickAccessOverlayMotion.Interpolate(start, end, late);

        Assert.Equal(0.5, middle, 3);
        Assert.True(earlyPoint.X - start.X < latePoint.X - earlyPoint.X);
        Assert.InRange(Math.Abs((end.X - latePoint.X) - (earlyPoint.X - start.X)), 0, 1);
        Assert.Equal(0.75, QuickAccessOverlayMotion.ExitOpacity(0.25), 3);
        Assert.Equal(0.25, QuickAccessOverlayMotion.ExitOpacity(0.75), 3);
    }

    [Fact]
    public void RestackUsesAQuickEaseOutAndLandsExactlyOnTheOpenSlot()
    {
        var start = new SD.Point(1700, 636);
        var target = new SD.Point(1700, 768);

        SD.Point halfway = QuickAccessOverlayMotion.Interpolate(
            start,
            target,
            QuickAccessOverlayMotion.EaseOutCubic(0.5));
        SD.Point finished = QuickAccessOverlayMotion.Interpolate(
            start,
            target,
            QuickAccessOverlayMotion.EaseOutCubic(1));

        Assert.True(halfway.Y > start.Y + (target.Y - start.Y) / 2);
        Assert.Equal(target, finished);
        Assert.True(QuickAccessOverlayMotion.RestackDurationMs < QuickAccessOverlayMotion.ExitDurationMs);
        Assert.Equal(8, QuickAccessOverlayMotion.FrameIntervalMs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void MotionProgressIsClamped(double input, double expected)
    {
        Assert.Equal(expected, QuickAccessOverlayMotion.EaseInOutSine(input), 3);
        Assert.Equal(expected, QuickAccessOverlayMotion.EaseOutCubic(input), 3);
        Assert.Equal(1 - expected, QuickAccessOverlayMotion.ExitOpacity(input), 3);
    }
}
