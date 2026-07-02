using System.Drawing;
using WinShot.Capture;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class AllInOneDragLayoutTests
{
    [Fact]
    public void CreatePixelRectangle_NormalizesFreeformDrag()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(10, 20),
            new SD.Point(4, 8),
            aspectRatio: null);

        Assert.Equal(new Rectangle(4, 8, 6, 12), rect);
    }

    [Fact]
    public void CreatePixelRectangle_ExtendsHeightWhenAspectLocked()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(0, 0),
            new SD.Point(160, 10),
            aspectRatio: 16d / 9d);

        Assert.Equal(new Rectangle(0, 0, 160, 90), rect);
    }

    [Fact]
    public void CreatePixelRectangle_PreservesDragDirectionWhenAspectLocked()
    {
        var rect = AllInOneDragLayout.CreatePixelRectangle(
            new SD.Point(10, 10),
            new SD.Point(4, 2),
            aspectRatio: 1);

        Assert.Equal(new Rectangle(2, 2, 8, 8), rect);
    }

}
