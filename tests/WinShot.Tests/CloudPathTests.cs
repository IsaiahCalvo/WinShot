using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class CloudPathTests
{
    [Fact]
    public void ForRectangle_IsClosedAndFilled()
    {
        var g = CloudPath.ForRectangle(new Rect(10, 20, 120, 80), intensity: 2);
        Assert.NotEmpty(g.Figures);
        Assert.True(g.Figures[0].IsClosed);
        Assert.True(g.Figures[0].IsFilled);
        Assert.Contains(g.Figures[0].Segments, s => s is QuadraticBezierSegment);
    }

    [Fact]
    public void ForRectangle_TinyRect_StillProducesGeometry()
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 4, 4), intensity: 1);
        Assert.False(g.Bounds.IsEmpty);
        Assert.True(g.Bounds.Width > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(20)]
    public void ForRectangle_AnyIntensity_StaysFinite(double intensity)
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 80, 40), intensity);
        Assert.False(double.IsNaN(g.Bounds.X));
        Assert.False(double.IsNaN(g.Bounds.Width));
    }
}
