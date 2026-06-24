using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class FastSelectorGuideLayoutTests
{
    [Fact]
    public void Calculate_CreatesFourCrosshairSegmentsAroundCursor()
    {
        var guides = FastSelectorGuideLayout.Calculate(
            new SD.Size(200, 120),
            new SD.Point(80, 50),
            gap: 10);

        Assert.True(guides.IsVisible);
        Assert.Equal(new SD.Point(80, 50), guides.Cursor);
        Assert.Equal(new SD.Point(0, 50), guides.LeftStart);
        Assert.Equal(new SD.Point(70, 50), guides.LeftEnd);
        Assert.Equal(new SD.Point(90, 50), guides.RightStart);
        Assert.Equal(new SD.Point(199, 50), guides.RightEnd);
        Assert.Equal(new SD.Point(80, 0), guides.TopStart);
        Assert.Equal(new SD.Point(80, 40), guides.TopEnd);
        Assert.Equal(new SD.Point(80, 60), guides.BottomStart);
        Assert.Equal(new SD.Point(80, 119), guides.BottomEnd);
    }

    [Fact]
    public void Calculate_ClampsGapNearEdges()
    {
        var guides = FastSelectorGuideLayout.Calculate(
            new SD.Size(40, 30),
            new SD.Point(3, 27),
            gap: 10);

        Assert.True(guides.IsVisible);
        Assert.Equal(new SD.Point(0, 27), guides.LeftStart);
        Assert.Equal(new SD.Point(0, 27), guides.LeftEnd);
        Assert.Equal(new SD.Point(13, 27), guides.RightStart);
        Assert.Equal(new SD.Point(39, 27), guides.RightEnd);
        Assert.Equal(new SD.Point(3, 0), guides.TopStart);
        Assert.Equal(new SD.Point(3, 17), guides.TopEnd);
        Assert.Equal(new SD.Point(3, 29), guides.BottomStart);
        Assert.Equal(new SD.Point(3, 29), guides.BottomEnd);
    }

    [Theory]
    [InlineData(0, 30, 5, 5)]
    [InlineData(40, 0, 5, 5)]
    [InlineData(40, 30, -1, 5)]
    [InlineData(40, 30, 5, -1)]
    [InlineData(40, 30, 40, 5)]
    [InlineData(40, 30, 5, 30)]
    public void Calculate_HidesWhenCanvasOrCursorIsInvalid(int width, int height, int x, int y)
    {
        var guides = FastSelectorGuideLayout.Calculate(
            new SD.Size(width, height),
            new SD.Point(x, y),
            gap: 10);

        Assert.False(guides.IsVisible);
    }
}
