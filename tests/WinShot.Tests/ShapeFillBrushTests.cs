using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ShapeFillBrushTests
{
    [Fact]
    public void Create_ReturnsNullForNoFill()
    {
        Assert.Null(ShapeFillBrush.Create(ShapeFillMode.None, Colors.Red));
    }

    [Fact]
    public void Create_ReturnsQuarterOpacityColorForQuarterFill()
    {
        var brush = Assert.IsType<SolidColorBrush>(
            ShapeFillBrush.Create(ShapeFillMode.Quarter, Color.FromRgb(10, 20, 30)));

        Assert.Equal(Color.FromArgb(0x40, 10, 20, 30), brush.Color);
    }

    [Fact]
    public void Create_ReturnsSolidColorForSolidFill()
    {
        var brush = Assert.IsType<SolidColorBrush>(
            ShapeFillBrush.Create(ShapeFillMode.Solid, Color.FromRgb(10, 20, 30)));

        Assert.Equal(Color.FromRgb(10, 20, 30), brush.Color);
    }

    [Fact]
    public void CreateFromName_FallsBackToNoFillForUnknownValues()
    {
        Assert.Null(ShapeFillBrush.CreateFromName("unsupported", Colors.Red));
    }
}
