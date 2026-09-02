using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ArrowheadGeometryTests
{
    [Fact]
    public void None_ProducesShaftOnly()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 4, ArrowheadStyle.None);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Single(pg.Figures);
        Assert.False(pg.Figures[0].IsFilled);
    }

    [Fact]
    public void SolidTriangle_AddsFilledHeadAtTheTip()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 4, ArrowheadStyle.SolidTriangle);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.True(pg.Figures[1].IsFilled);
        Assert.True(pg.Figures[1].IsClosed);
        Assert.True(pg.Bounds.Contains(new Point(100, 0)) || pg.Bounds.Right >= 100);
    }

    [Fact]
    public void DoubleLegacy_PutsHeadsOnBothEnds()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(80, 0), 4, ArrowStyle.Double);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(3, pg.Figures.Count);
        Assert.True(pg.Figures[1].IsFilled);
        Assert.True(pg.Figures[2].IsFilled);
    }

    /// <summary>
    /// Survey strokes the V at max(2, strokeWidth) with round caps and no dash
    /// (lineRenderHelpers.js:110-176). One WPF Path carries a single StrokeThickness and a
    /// single dash array, so the head is emitted as that pen's WIDENED OUTLINE, filled -
    /// same weight, same round ends, and immune to the shaft's dash.
    /// </summary>
    [Fact]
    public void VShape_IsAWidenedFilledOutlineAtTheTip()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(50, 0), 3, ArrowheadStyle.VShape);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.True(pg.Figures[1].IsClosed);
        Assert.True(pg.Figures[1].IsFilled);
        // The V's arms still meet AT the tip, which the outline wraps.
        Assert.True(pg.Bounds.Right >= 50);
    }

    /// <summary>Survey's 2px floor: a head on a 1px line still reads (lineRenderHelpers.js:110).</summary>
    [Fact]
    public void ThinLine_StillDrawsItsHeadAtTheTwoPixelFloor()
    {
        // A horizontal arrow's tick is vertical, so the widened outline's X extent IS the pen.
        var thin = (PathGeometry)AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 1, ArrowheadStyle.HorizontalLine);
        var tick = new PathGeometry { Figures = { thin.Figures[1] } };
        Assert.Equal(2, tick.Bounds.Width, 2);

        var fat = (PathGeometry)AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 4, ArrowheadStyle.HorizontalLine);
        var fatTick = new PathGeometry { Figures = { fat.Figures[1] } };
        Assert.Equal(4, fatTick.Bounds.Width, 2);
    }

    [Fact]
    public void OpenCircle_CentersOnTheTip()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(10, 10), new Point(10, 80), 2, ArrowheadStyle.OpenCircle);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        // The widened outline is a RING, not a disc: the tip itself stays hollow.
        Assert.True(pg.Figures[1].IsFilled);
        Assert.False(pg.FillContains(new Point(10, 80)));
        Assert.True(pg.Bounds.Contains(new Point(10, 80)) ||
                    (pg.Bounds.Top <= 80 && pg.Bounds.Bottom >= 80 && pg.Bounds.Left <= 10 && pg.Bounds.Right >= 10));
    }

    [Fact]
    public void OpenTriangle_IsAWidenedOutlineNotASolidTriangle()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(40, 40), 5, ArrowheadStyle.OpenTriangle);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.True(pg.Figures[1].IsClosed);
        Assert.True(pg.Figures[1].IsFilled);
        // Unlike the solid triangle, the shaft is trimmed by headSize/3 for BOTH triangles,
        // so the outline still reaches the tip.
        Assert.True(pg.Bounds.Right >= 40);
    }

    [Fact]
    public void HorizontalLine_IsAPerpendicularTick()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 4, ArrowheadStyle.HorizontalLine);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        var tick = new PathGeometry { Figures = { pg.Figures[1] } };
        // A vertical tick on a horizontal arrow: tall, pen-wide, and centred on the axis.
        Assert.True(tick.Bounds.Height > tick.Bounds.Width);
        Assert.True(Math.Abs(tick.Bounds.Top + tick.Bounds.Bottom) < 0.01);
        Assert.True(tick.Bounds.Right >= 100);
    }

    [Fact]
    public void DegenerateArrow_DoesNotThrow()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(5, 5), new Point(5, 5), 4, ArrowheadStyle.SolidTriangle);
        Assert.NotNull(g);
        Assert.False(g.Bounds.IsEmpty);
    }

    [Fact]
    public void StartAndEndHeads_CanDiffer()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(60, 0), 4,
            ArrowheadStyle.OpenCircle, ArrowheadStyle.HorizontalLine);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(3, pg.Figures.Count);
    }

    [Theory]
    [InlineData(ArrowheadStyle.None)]
    [InlineData(ArrowheadStyle.SolidTriangle)]
    [InlineData(ArrowheadStyle.VShape)]
    [InlineData(ArrowheadStyle.OpenCircle)]
    [InlineData(ArrowheadStyle.OpenTriangle)]
    [InlineData(ArrowheadStyle.HorizontalLine)]
    public void EveryStyle_ProducesFiniteGeometry(ArrowheadStyle style)
    {
        var g = AnnotationFactory.ArrowGeometry(new Point(3, 7), new Point(90, 40), 6, style);
        Assert.True(g.Bounds.Width >= 0);
        Assert.True(g.Bounds.Height >= 0);
        Assert.False(double.IsNaN(g.Bounds.X));
        Assert.False(double.IsNaN(g.Bounds.Y));
    }
}
