using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class PreviousRegionTests
{
    [Theory]
    [InlineData("10,20,300,400", 10, 20, 300, 400)]
    [InlineData(" -5, 20, 300, 400 ", -5, 20, 300, 400)]
    public void TryParse_AcceptsSavedScreenRegion(string value, int x, int y, int width, int height)
    {
        Assert.True(PreviousRegion.TryParse(value, out Rectangle rect));
        Assert.Equal(new Rectangle(x, y, width, height), rect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("1,2,0,4")]
    [InlineData("1,2,3,0")]
    [InlineData("1,2,-3,4")]
    [InlineData("left,2,3,4")]
    public void TryParse_RejectsInvalidSavedRegion(string value)
    {
        Assert.False(PreviousRegion.TryParse(value, out Rectangle rect));
        Assert.Equal(default, rect);
    }

    [Fact]
    public void Format_UsesScreenPixelRectangle()
    {
        string value = PreviousRegion.Format(new Rectangle(-10, 25, 640, 480));

        Assert.Equal("-10,25,640,480", value);
    }
}
