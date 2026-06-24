using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class FastSelectorLoupeLayoutTests
{
    [Fact]
    public void Calculate_CentersSourceAndLoupeAroundCursor()
    {
        var layout = FastSelectorLoupeLayout.Calculate(
            clientSize: new SD.Size(800, 600),
            virtualScreen: new SD.Rectangle(0, 0, 800, 600),
            cursorClient: new SD.Point(100, 100),
            cursorScreen: new SD.Point(100, 100),
            loupeSize: 120,
            zoom: 8);

        Assert.True(layout.IsVisible);
        Assert.Equal(new SD.Rectangle(93, 93, 15, 15), layout.SourceScreen);
        Assert.Equal(new SD.Rectangle(124, 152, 120, 120), layout.Bounds);
        Assert.Equal(new SD.Point(184, 212), layout.CrosshairCenter);
        Assert.Equal("100, 100", layout.Coordinates);
    }

    [Fact]
    public void Calculate_ClampsSourceAtVirtualScreenEdge()
    {
        var layout = FastSelectorLoupeLayout.Calculate(
            clientSize: new SD.Size(800, 600),
            virtualScreen: new SD.Rectangle(-100, -50, 800, 600),
            cursorClient: new SD.Point(0, 0),
            cursorScreen: new SD.Point(-100, -50),
            loupeSize: 120,
            zoom: 8);

        Assert.True(layout.IsVisible);
        Assert.Equal(new SD.Rectangle(-100, -50, 15, 15), layout.SourceScreen);
        Assert.Equal(new SD.Point(28, 56), layout.CrosshairCenter);
        Assert.Equal("-100, -50", layout.Coordinates);
    }

    [Fact]
    public void Calculate_FlipsLoupeAwayFromRightAndBottomEdges()
    {
        var layout = FastSelectorLoupeLayout.Calculate(
            clientSize: new SD.Size(300, 220),
            virtualScreen: new SD.Rectangle(0, 0, 300, 220),
            cursorClient: new SD.Point(290, 200),
            cursorScreen: new SD.Point(290, 200),
            loupeSize: 120,
            zoom: 8);

        Assert.True(layout.IsVisible);
        Assert.Equal(new SD.Rectangle(146, 24, 120, 120), layout.Bounds);
    }

    [Theory]
    [InlineData(0, 100, 10, 10)]
    [InlineData(100, 0, 10, 10)]
    [InlineData(100, 100, -1, 10)]
    [InlineData(100, 100, 100, 10)]
    [InlineData(100, 100, 10, -1)]
    [InlineData(100, 100, 10, 100)]
    public void Calculate_HidesWhenClientOrCursorIsInvalid(int width, int height, int x, int y)
    {
        var layout = FastSelectorLoupeLayout.Calculate(
            clientSize: new SD.Size(width, height),
            virtualScreen: new SD.Rectangle(0, 0, 100, 100),
            cursorClient: new SD.Point(x, y),
            cursorScreen: new SD.Point(x, y),
            loupeSize: 120,
            zoom: 8);

        Assert.False(layout.IsVisible);
    }
}
