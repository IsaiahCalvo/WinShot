using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class CropSelectionLayoutTests
{
    [Fact]
    public void Calculate_SnapsFreeCropEdgesToImageBounds()
    {
        var rect = CropSelectionLayout.Calculate(
            new Size(400, 300),
            new Point(5, 7),
            new Point(396, 294),
            aspectRatio: null,
            snapDistance: 8);

        Assert.Equal(new Rect(0, 0, 400, 300), rect);
    }

    [Fact]
    public void Calculate_PreservesFixedRatioAndFitsInsideImage()
    {
        var rect = CropSelectionLayout.Calculate(
            new Size(400, 300),
            new Point(100, 100),
            new Point(360, 250),
            aspectRatio: 16.0 / 9.0,
            snapDistance: 8);

        AssertRectNear(new Rect(100, 100, 266.6666666666667, 150), rect);
    }

    [Fact]
    public void Calculate_TranslatesFixedRatioCropWhenNearEdge()
    {
        var rect = CropSelectionLayout.Calculate(
            new Size(400, 300),
            new Point(40, 40),
            new Point(393, 238.5625),
            aspectRatio: 16.0 / 9.0,
            snapDistance: 8);

        Assert.Equal(new Rect(47, 40, 353, 198.5625), rect);
    }

    [Fact]
    public void Calculate_ClampsDragPointAndHandlesReverseDrag()
    {
        var rect = CropSelectionLayout.Calculate(
            new Size(400, 300),
            new Point(300, 220),
            new Point(-20, -10),
            aspectRatio: 1,
            snapDistance: 8);

        Assert.Equal(new Rect(80, 0, 220, 220), rect);
    }

    [Fact]
    public void Calculate_HandlesInvalidInputs()
    {
        var rect = CropSelectionLayout.Calculate(
            new Size(0, 0),
            new Point(10, 20),
            new Point(30, 40),
            aspectRatio: 0,
            snapDistance: -5);

        Assert.Equal(new Rect(1, 1, 0, 0), rect);
    }

    private static void AssertRectNear(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 9);
        Assert.Equal(expected.Y, actual.Y, precision: 9);
        Assert.Equal(expected.Width, actual.Width, precision: 9);
        Assert.Equal(expected.Height, actual.Height, precision: 9);
    }
}
