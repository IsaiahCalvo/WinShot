using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class QuickAccessOverlayMotionTests
{
    private const int Gutter = 12;

    [Theory]
    [InlineData("bottom-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom")]
    public void ACardAnchoredToTheBottomStartsBelowItsSlotAndOutsideTheWorkingArea(string position)
    {
        var workingArea = new SD.Rectangle(-1920, -120, 1920, 1040);
        var bounds = new SD.Rectangle(-220, 780, 192, 120);

        SD.Point origin = QuickAccessOverlayMotion.EnterOrigin(workingArea, bounds, position, Gutter);

        Assert.Equal(bounds.Left, origin.X);
        Assert.True(origin.Y > bounds.Top, "It has to arrive from below its slot.");
        // Almost entirely past the edge — only the top hairline of the card is on screen.
        Assert.True(origin.Y + bounds.Height > workingArea.Bottom);
    }

    [Theory]
    [InlineData("bottom-right")]
    [InlineData("top")]
    public void TravelIsOneCardAndOneGutterHoweverDeepTheCardSitsInTheStack(string position)
    {
        var workingArea = new SD.Rectangle(-1920, -120, 1920, 1040);
        // Fourth slot down (bottom) / fourth slot in (top): far from either edge.
        var bounds = new SD.Rectangle(-220, 400, 192, 120);
        int expected = bounds.Height + Gutter;

        SD.Point origin = QuickAccessOverlayMotion.EnterOrigin(workingArea, bounds, position, Gutter);

        Assert.Equal(expected, Math.Abs(origin.Y - bounds.Top));
    }

    [Theory]
    [InlineData("bottom-right", 850)]
    [InlineData("top", -200)]
    public void TheCardNearestTheEdgeNeverRunsFurtherThanTheEdge(string position, int slotTop)
    {
        var workingArea = new SD.Rectangle(-1920, -120, 1920, 1040);
        var bounds = new SD.Rectangle(-220, slotTop, 192, 120);

        SD.Point origin = QuickAccessOverlayMotion.EnterOrigin(workingArea, bounds, position, Gutter);

        Assert.InRange(origin.Y, workingArea.Top - bounds.Height, workingArea.Bottom);
        Assert.True(Math.Abs(origin.Y - bounds.Top) < bounds.Height + Gutter);
    }

    [Theory]
    [InlineData("left", true)]
    [InlineData("right", false)]
    public void ASideAnchoredCardStartsOffThatSameEdge(string position, bool offLeft)
    {
        var workingArea = new SD.Rectangle(-1920, -120, 1920, 1040);
        var bounds = new SD.Rectangle(-1904, 340, 192, 120);

        SD.Point origin = QuickAccessOverlayMotion.EnterOrigin(workingArea, bounds, position, Gutter);

        // Side anchors slide in horizontally while they stack vertically, so their travel
        // never grew with depth and they keep starting clear of their own edge.
        Assert.Equal(bounds.Top, origin.Y);
        if (offLeft)
            Assert.True(origin.X + bounds.Width <= workingArea.Left);
        else
            Assert.True(origin.X >= workingArea.Right);
    }

    [Fact]
    public void TheEntranceOvershootsItsSlotAndThenSettlesExactlyOnIt()
    {
        var start = new SD.Point(1700, 1040);
        var end = new SD.Point(1700, 900);

        double peak = Enumerable.Range(1, 99)
            .Select(i => QuickAccessOverlayMotion.EaseOutBack(i / 100d))
            .Max();

        Assert.True(peak > 1d, "EaseOutBack must go past its target — that is the settle.");
        Assert.Equal(0d, QuickAccessOverlayMotion.EaseOutBack(0d), 6);
        Assert.Equal(1d, QuickAccessOverlayMotion.EaseOutBack(1d), 6);
        // Clamped interpolation would flatten the overshoot into a stop at the slot.
        Assert.True(QuickAccessOverlayMotion.InterpolateUnclamped(start, end, peak).Y < end.Y);
        Assert.Equal(end, QuickAccessOverlayMotion.InterpolateUnclamped(start, end, 1d));
        Assert.Equal(start, QuickAccessOverlayMotion.InterpolateUnclamped(start, end, 0d));
    }

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

    /// <summary>
    /// Why BeginEnterMotion, BeginExitMotion and ReflowOpenWindowsAnimated all read the
    /// working area the card was PLACED against rather than re-deriving one from Bounds.
    /// Screen.FromRectangle picks the monitor holding most of the rectangle, and a card at
    /// its entrance origin is almost entirely off its own monitor — on Isaiah's rig, where
    /// the monitors sit above the laptop, that answer is the screen below and the card
    /// restacks onto the wrong display.
    /// </summary>
    [Fact]
    public void TheEntranceOriginSitsMostlyOutsideTheMonitorTheCardWasPlacedAgainst()
    {
        // The monitor above the laptop: its working area ENDS where the laptop's begins.
        var above = new SD.Rectangle(0, -1440, 2560, 1400);
        var size = new SD.Size(288, 180);

        SD.Point slot = QuickAccessOverlayLayout.Place(above, size, "bottom-right", 0, 144);
        SD.Point origin = QuickAccessOverlayMotion.EnterOrigin(
            above, new SD.Rectangle(slot, size), "bottom-right", QuickAccessOverlayLayout.StackGutter(144));

        SD.Rectangle starting = new(origin, size);
        SD.Rectangle inside = SD.Rectangle.Intersect(above, starting);
        long insideArea = (long)inside.Width * inside.Height;
        long total = (long)starting.Width * starting.Height;
        Assert.True(insideArea * 2 < total,
            $"entrance origin {starting} keeps {insideArea}/{total} px on {above}");
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
