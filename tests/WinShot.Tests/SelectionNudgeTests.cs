using WinShot.Capture;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Keyboard adjustment of a framed selection. Before this the selector's key handler knew
/// exactly three keys — Escape, Enter and Alt — so a selection that was two pixels off had to
/// be redrawn or dragged by hand.
/// </summary>
public class SelectionNudgeTests
{
    private static readonly SD.Rectangle Desktop = new(0, 0, 1920, 1080);
    private static readonly SD.Rectangle Region = new(100, 200, 400, 300);

    private static SD.Rectangle? Apply(WF.Keys key, bool shift = false, bool control = false, SD.Rectangle? from = null) =>
        SelectionNudge.Apply(from ?? Region, key, shift, control, Desktop);

    [Fact]
    public void ArrowsMoveByOnePixelAndShiftMovesByTen()
    {
        Assert.Equal(new SD.Rectangle(99, 200, 400, 300), Apply(WF.Keys.Left));
        Assert.Equal(new SD.Rectangle(100, 201, 400, 300), Apply(WF.Keys.Down));
        Assert.Equal(new SD.Rectangle(110, 200, 400, 300), Apply(WF.Keys.Right, shift: true));
        Assert.Equal(new SD.Rectangle(100, 190, 400, 300), Apply(WF.Keys.Up, shift: true));
    }

    [Fact]
    public void ControlArrowsResizeInsteadOfMoving()
    {
        Assert.Equal(new SD.Rectangle(100, 200, 401, 300), Apply(WF.Keys.Right, control: true));
        Assert.Equal(new SD.Rectangle(100, 200, 400, 290), Apply(WF.Keys.Up, shift: true, control: true));
        Assert.Equal(new SD.Rectangle(100, 200, 390, 300), Apply(WF.Keys.Left, shift: true, control: true));
    }

    [Fact]
    public void BracketsShrinkAndGrowEverySideAtOnce()
    {
        Assert.Equal(new SD.Rectangle(101, 201, 398, 298), Apply(WF.Keys.OemOpenBrackets));
        Assert.Equal(new SD.Rectangle(99, 199, 402, 302), Apply(WF.Keys.OemCloseBrackets));
        Assert.Equal(new SD.Rectangle(110, 210, 380, 280), Apply(WF.Keys.OemOpenBrackets, shift: true));
    }

    [Fact]
    public void AMoveKeepsItsSizeAtTheDesktopEdge()
    {
        var atEdge = new SD.Rectangle(0, 0, 400, 300);
        SD.Rectangle? moved = Apply(WF.Keys.Left, shift: true, from: atEdge);

        Assert.Equal(atEdge, moved);
    }

    [Fact]
    public void ShrinkingCannotCollapseTheSelection()
    {
        var tiny = new SD.Rectangle(500, 500, 2, 2);

        Assert.Equal(tiny, Apply(WF.Keys.OemOpenBrackets, from: tiny));
        Assert.Equal(tiny, Apply(WF.Keys.OemOpenBrackets, shift: true, from: tiny));
    }

    [Fact]
    public void ResizingCannotCollapseTheSelectionEither()
    {
        var tiny = new SD.Rectangle(500, 500, 1, 1);

        Assert.Equal(tiny, Apply(WF.Keys.Left, shift: true, control: true, from: tiny));
    }

    [Fact]
    public void NudgingWorksOnADesktopThatStartsAtNegativeCoordinates()
    {
        // Two monitors above the laptop: the virtual desktop's origin is negative, and a
        // clamp written against (0,0) would refuse to move a selection up there at all.
        var desktop = new SD.Rectangle(-1920, -1080, 3840, 2160);
        var region = new SD.Rectangle(-1800, -1000, 200, 200);

        Assert.Equal(
            new SD.Rectangle(-1810, -1000, 200, 200),
            SelectionNudge.Apply(region, WF.Keys.Left, shift: true, control: false, desktop));
    }

    [Theory]
    [InlineData(WF.Keys.Left)]
    [InlineData(WF.Keys.OemCloseBrackets)]
    public void HandlesReportsTheKeysApplyAnswers(WF.Keys key)
    {
        Assert.True(SelectionNudge.Handles(key));
        Assert.NotNull(Apply(key));
    }

    [Theory]
    [InlineData(WF.Keys.Enter)]
    [InlineData(WF.Keys.Space)]
    [InlineData(WF.Keys.C)]
    public void EveryOtherKeyIsLeftToItsOwnHandler(WF.Keys key)
    {
        Assert.False(SelectionNudge.Handles(key));
        Assert.Null(Apply(key));
    }

    [Fact]
    public void ShiftMeansTheSameThingHereAsItDoesInTheEditor()
    {
        Assert.Equal(1, SelectionNudge.StepFor(shift: false));
        Assert.Equal(10, SelectionNudge.StepFor(shift: true));
    }
}
