using System.Drawing;
using WinShot.Editor.Background;
using Xunit;

namespace WinShot.Tests;

public class BackgroundLayoutTests
{
    [Fact]
    public void Calculate_AutoCanvasHugsImageWithPadding()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 200),
            padding: 50,
            aspectRatio: null);

        Assert.Equal(new Size(500, 300), layout.CanvasSize);
        Assert.Equal(50, layout.Margin);
    }

    [Fact]
    public void Calculate_WideRatioExpandsWidthOnly()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 300),
            padding: 50,
            aspectRatio: 16d / 9d);

        Assert.Equal(new Size(712, 400), layout.CanvasSize);
        Assert.Equal(50, layout.Margin);
    }

    [Fact]
    public void Calculate_TallRatioExpandsHeightOnly()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(400, 300),
            padding: 50,
            aspectRatio: 1);

        Assert.Equal(new Size(500, 500), layout.CanvasSize);
    }

    [Fact]
    public void Calculate_ClampsNegativeInputsToSafeMinimums()
    {
        var layout = BackgroundLayout.Calculate(
            new Size(0, -5),
            padding: -10,
            aspectRatio: 0);

        Assert.Equal(new Size(1, 1), layout.SourceSize);
        Assert.Equal(new Size(1, 1), layout.CanvasSize);
        Assert.Equal(0, layout.Margin);
    }
}
