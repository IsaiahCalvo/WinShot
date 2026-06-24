using System.Drawing;
using System.Windows;
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

    [Fact]
    public void CreateDipRect_UsesSameAspectLockRulesForWpfSelector()
    {
        Rect rect = AllInOneDragLayout.CreateDipRect(
            new System.Windows.Point(0, 0),
            new System.Windows.Point(30, 40),
            aspectRatio: 4d / 3d);

        Assert.Equal(0, rect.X, precision: 10);
        Assert.Equal(0, rect.Y, precision: 10);
        Assert.Equal(53.3333333333, rect.Width, precision: 10);
        Assert.Equal(40, rect.Height, precision: 10);
    }
}
