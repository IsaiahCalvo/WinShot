using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class SpotlightLayoutTests
{
    [Fact]
    public void Calculate_ClampsHoleToImageBounds()
    {
        var layout = SpotlightLayout.Calculate(
            new Size(100, 80),
            new Rect(-10, 5, 30, 20));

        Assert.Equal(new Size(100, 80), layout.Outer);
        Assert.Equal(new Rect(0, 5, 20, 20), layout.Hole);
    }

    [Fact]
    public void Calculate_PreservesNormalizedReverseDragHole()
    {
        var layout = SpotlightLayout.Calculate(
            new Size(100, 80),
            new Rect(new Point(90, 70), new Point(30, 20)));

        Assert.Equal(new Rect(30, 20, 60, 50), layout.Hole);
    }

    [Fact]
    public void Calculate_UsesNearestPixelForFullyOutsideHole()
    {
        var layout = SpotlightLayout.Calculate(
            new Size(100, 80),
            new Rect(120, 90, 20, 20));

        Assert.Equal(new Rect(99, 79, 1, 1), layout.Hole);
    }

    [Fact]
    public void Calculate_HandlesInvalidImageSize()
    {
        var layout = SpotlightLayout.Calculate(
            new Size(0, 0),
            new Rect(10, 20, 30, 40));

        Assert.Equal(new Size(1, 1), layout.Outer);
        Assert.Equal(new Rect(0, 0, 1, 1), layout.Hole);
    }
}
