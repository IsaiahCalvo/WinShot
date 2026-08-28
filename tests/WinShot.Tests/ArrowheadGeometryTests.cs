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

    [Fact]
    public void VShape_IsAnOpenPolylineAtTheTip()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(50, 0), 3, ArrowheadStyle.VShape);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.False(pg.Figures[1].IsClosed);
        Assert.False(pg.Figures[1].IsFilled);
        Assert.Equal(2, pg.Figures[1].Segments.Count);
    }

    [Fact]
    public void OpenCircle_CentersOnTheTip()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(10, 10), new Point(10, 80), 2, ArrowheadStyle.OpenCircle);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.Contains(pg.Figures, f => f.IsClosed && !f.IsFilled && f.Segments.OfType<ArcSegment>().Any());
        Assert.True(pg.Bounds.Contains(new Point(10, 80)) ||
                    (pg.Bounds.Top <= 80 && pg.Bounds.Bottom >= 80 && pg.Bounds.Left <= 10 && pg.Bounds.Right >= 10));
    }

    [Fact]
    public void OpenTriangle_IsStrokedNotFilled()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(40, 40), 5, ArrowheadStyle.OpenTriangle);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        Assert.True(pg.Figures[1].IsClosed);
        Assert.False(pg.Figures[1].IsFilled);
    }

    [Fact]
    public void HorizontalLine_IsAPerpendicularTick()
    {
        var g = AnnotationFactory.ArrowGeometry(
            new Point(0, 0), new Point(100, 0), 4, ArrowheadStyle.HorizontalLine);
        var pg = Assert.IsType<PathGeometry>(g);
        Assert.Equal(2, pg.Figures.Count);
        var tick = pg.Figures[1];
        Assert.False(tick.IsClosed);
        var end = ((LineSegment)tick.Segments[0]).Point;
        Assert.True(Math.Abs(tick.StartPoint.X - end.X) < 0.01, "tick should be vertical on a horizontal arrow");
        Assert.True(Math.Abs((tick.StartPoint.Y + end.Y) / 2 - 0) < 0.01);
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
