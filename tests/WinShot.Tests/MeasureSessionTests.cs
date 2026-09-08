using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class MeasureSessionTests
{
    [Fact]
    public void TheWheelWalksToleranceInPowerToysStepsAndStopsAtTheEnds()
    {
        var session = new MeasureSession();
        Assert.Equal(EdgeSnap.DefaultTolerance, session.Tolerance);

        Assert.True(session.AdjustTolerance(120));
        Assert.Equal(45, session.Tolerance);
        Assert.True(session.AdjustTolerance(-120));
        Assert.True(session.AdjustTolerance(-120));
        Assert.Equal(15, session.Tolerance);

        session.SetTolerance(0);
        Assert.False(session.AdjustTolerance(-120));
        session.SetTolerance(255);
        Assert.False(session.AdjustTolerance(120));
    }

    [Fact]
    public void ToleranceClampsAtTheTopInsteadOfOvershooting()
    {
        var session = new MeasureSession();
        session.SetTolerance(250);
        Assert.True(session.AdjustTolerance(120));
        Assert.Equal(255, session.Tolerance);
    }

    [Fact]
    public void HoldsAccumulateAndBackspaceTakesTheNewestBack()
    {
        var session = new MeasureSession();
        session.Hold(new Rectangle(0, 0, 40, 10), horizontal: true, new Point(10, 5), dpi: 96);
        session.Hold(new Rectangle(0, 0, 10, 90), horizontal: false, new Point(5, 40), dpi: 96);

        Assert.Equal(2, session.Holds.Count);
        Assert.True(session.RemoveLastHold());
        Assert.Single(session.Holds);
        Assert.True(session.Holds[0].Horizontal);
        Assert.True(session.RemoveLastHold());
        Assert.False(session.RemoveLastHold());
    }

    [Fact]
    public void ARemovedGuideGoesOnItsOwnWithoutTakingTheRestOfTheCanvasWithIt()
    {
        // One misplaced guide used to cost every other guide AND every held span, because
        // clearing was the only removal path there was.
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.AddGuideX(200);
        session.AddGuideY(50);
        session.Hold(new Rectangle(0, 0, 40, 10), horizontal: true, new Point(10, 5), dpi: 96);

        Assert.True(session.RemoveLastGuide());

        Assert.Equal(new[] { 100, 200 }, session.GuidesX);
        Assert.Empty(session.GuidesY);
        Assert.Single(session.Holds);
    }

    [Fact]
    public void BackspaceTakesBackTheNewestThingWhicheverKindItWas()
    {
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.Hold(new Rectangle(0, 0, 40, 10), horizontal: true, new Point(10, 5), dpi: 96);
        session.AddGuideY(50);

        Assert.True(session.RemoveLast());   // the guide on Y
        Assert.Empty(session.GuidesY);
        Assert.Single(session.Holds);

        Assert.True(session.RemoveLast());   // the hold
        Assert.Empty(session.Holds);
        Assert.Equal(new[] { 100 }, session.GuidesX);

        Assert.True(session.RemoveLast());   // the guide on X
        Assert.False(session.RemoveLast());
        Assert.False(session.HasAnything);
    }

    [Fact]
    public void AGuideDraggedSomewhereElseIsStillTheOneBackspaceTakesBack()
    {
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.MoveGuide(vertical: true, 100, 640);

        Assert.True(session.RemoveLast());
        Assert.Empty(session.GuidesX);
    }

    [Fact]
    public void FirstEscapeClearsTheCanvasAndTheSecondOneDoesNotClaimTo()
    {
        var session = new MeasureSession();
        Assert.False(session.ClearOnEscape());

        session.AddGuideX(400);
        Assert.True(session.ClearOnEscape());
        Assert.False(session.ClearOnEscape());
    }

    [Fact]
    public void GuidesStaySortedAndNeverDoubleUpOnOneCoordinate()
    {
        var session = new MeasureSession();
        session.AddGuideX(900);
        session.AddGuideX(100);
        session.AddGuideX(900);
        session.AddGuideY(50);

        Assert.Equal(new[] { 100, 900 }, session.GuidesX);
        Assert.Equal(new[] { 50 }, session.GuidesY);
    }

    [Fact]
    public void AParallelPairLabelsItsOwnGap()
    {
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.AddGuideX(340);
        session.AddGuideX(900);

        var pairs = MeasureSession.Pairs(session.GuidesX).ToList();

        Assert.Equal(new[] { (100, 340), (340, 900) }, pairs);
    }

    [Fact]
    public void ASingleGuideHasNoPairAndSoNoLabel() =>
        Assert.Empty(MeasureSession.Pairs(new[] { 400 }));

    [Fact]
    public void AGuideIsGrabbableWithinToleranceAndNotBeyondIt()
    {
        var session = new MeasureSession();
        session.AddGuideX(400);
        session.AddGuideX(408);

        Assert.Equal(400, session.GuideNear(vertical: true, 402, 4));
        Assert.Equal(408, session.GuideNear(vertical: true, 407, 4));
        Assert.Null(session.GuideNear(vertical: true, 300, 4));
        Assert.Null(session.GuideNear(vertical: false, 400, 4));
    }

    [Fact]
    public void DraggingAGuideMovesItAndKeepsTheListSorted()
    {
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.AddGuideX(900);

        Assert.True(session.MoveGuide(vertical: true, 900, 50));
        Assert.Equal(new[] { 50, 100 }, session.GuidesX);
        Assert.False(session.MoveGuide(vertical: true, 777, 10));
    }

    [Fact]
    public void AGuideDraggedOntoAnotherStaysWhereItIsRatherThanStackingOnIt()
    {
        var session = new MeasureSession();
        session.AddGuideX(100);
        session.AddGuideX(140);

        Assert.False(session.MoveGuide(vertical: true, 140, 100));
        Assert.Equal(new[] { 100, 140 }, session.GuidesX);
        // And the gap the pair labels is still a real one, not a zero.
        Assert.Equal(new[] { (100, 140) }, MeasureSession.Pairs(session.GuidesX).ToArray());
    }

    [Fact]
    public void CopyFallsBackToTheLiveCrossWhenNothingIsHeld()
    {
        var session = new MeasureSession();

        Assert.Equal("296 × 118 · ~2.51 : 1", session.CopyText(new Rectangle(0, 0, 296, 118), 96));
        Assert.Equal(string.Empty, session.CopyText(null, 96));
    }

    [Fact]
    public void CopyWritesOneLinePerHeldSpanInTheOrderTheyWereHeld()
    {
        var session = new MeasureSession();
        session.Hold(new Rectangle(0, 0, 40, 10), horizontal: true, new Point(10, 5), dpi: 96);
        session.Hold(new Rectangle(0, 0, 10, 90), horizontal: false, new Point(5, 40), dpi: 96);

        Assert.Equal($"40{Environment.NewLine}90", session.CopyText(new Rectangle(0, 0, 5, 5), 96));
    }

    [Fact]
    public void EachHeldSpanIsCopiedWithTheDpiOfTheMonitorItWasHeldOn()
    {
        // A mixed-DPI desktop: the paint labels each hold with its own monitor's dip, so the
        // clipboard must not relabel them all with whichever monitor the pointer ended on.
        var session = new MeasureSession();
        session.Hold(new Rectangle(0, 0, 100, 10), horizontal: true, new Point(5, 5), dpi: 144);
        session.Hold(new Rectangle(2000, 0, 100, 10), horizontal: true, new Point(2005, 5), dpi: 96);

        string[] lines = session.CopyText(null, liveDpi: 144).Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.Contains("67 dip", lines[0]);
        Assert.Equal("100", lines[1]);
    }

    [Fact]
    public void ResetLeavesNothingBehindForTheNextSession()
    {
        var session = new MeasureSession();
        session.Hold(new Rectangle(0, 0, 40, 10), horizontal: true, new Point(10, 5), dpi: 96);
        session.AddGuideY(20);
        session.ToggleContrastScale();

        session.Reset();

        Assert.False(session.HasAnything);
        Assert.False(session.UseApca);
    }
}
