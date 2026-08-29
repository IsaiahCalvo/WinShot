using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ShapeConstraintTests
{
    [Fact]
    public void BoxFromDrag_NoSquare_KeepsQuadrant()
    {
        var r = ShapeConstraint.BoxFromDrag(new Point(10, 10), new Point(4, 20), square: false);
        Assert.Equal(new Rect(4, 10, 6, 10), r);
    }

    [Fact]
    public void BoxFromDrag_Square_UsesLongerAxisAndKeepsSign()
    {
        var r = ShapeConstraint.BoxFromDrag(new Point(10, 10), new Point(40, 20), square: true);
        Assert.Equal(new Rect(10, 10, 30, 30), r);

        var upLeft = ShapeConstraint.BoxFromDrag(new Point(50, 50), new Point(10, 40), square: true);
        Assert.Equal(new Rect(10, 10, 40, 40), upLeft);
    }

    [Fact]
    public void SnapLineEnd_NearEastPullsToEastPreservingLength()
    {
        var snapped = ShapeConstraint.SnapLineEnd(new Point(0, 0), new Point(10, 1), snap: true);
        Assert.Equal(Math.Sqrt(101), snapped.X, 3);
        Assert.Equal(0, snapped.Y, 3);
    }

    [Theory]
    [InlineData(10, 0, 10, 0)]       // east stays east
    [InlineData(10, 10, 10, 10)]     // 45° stays
    [InlineData(0, 10, 0, 10)]       // north
    [InlineData(-10, 0, -10, 0)]     // west
    public void SnapLineEnd_Exact45RaysStay(double dx, double dy, double expectDx, double expectDy)
    {
        var snapped = ShapeConstraint.SnapLineEnd(new Point(0, 0), new Point(dx, dy), snap: true);
        Assert.Equal(expectDx, snapped.X, 3);
        Assert.Equal(expectDy, snapped.Y, 3);
    }

    [Fact]
    public void SnapLineEnd_OffDoesNothing()
    {
        var p = new Point(12, 3);
        Assert.Equal(p, ShapeConstraint.SnapLineEnd(new Point(0, 0), p, snap: false));
    }

    [Fact]
    public void UniformResize_CornerAveragesScaleFromOpposite()
    {
        var original = new Rect(0, 0, 10, 20);
        var unconstrained = new Rect(0, 0, 20, 20); // BR drag, sx=2 sy=1 → avg 1.5
        var next = ShapeConstraint.UniformResize(original, handle: 2, unconstrained);
        Assert.Equal(0, next.X, 3);
        Assert.Equal(0, next.Y, 3);
        Assert.Equal(15, next.Width, 3);
        Assert.Equal(30, next.Height, 3);
    }

    [Fact]
    public void UniformResize_TopEdgePreservesAspectCentered()
    {
        var original = new Rect(0, 0, 20, 10);
        var unconstrained = new Rect(0, 5, 20, 5); // T dragged down, height 5
        var next = ShapeConstraint.UniformResize(original, handle: 4, unconstrained);
        Assert.Equal(10, next.Width, 3);
        Assert.Equal(5, next.Height, 3);
        Assert.Equal(5, next.X, 3);
        Assert.Equal(5, next.Y, 3);
    }

    [Theory]
    [InlineData(44, 45)]
    [InlineData(46, 45)]
    [InlineData(10, 10)]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    public void SnapAngleToNearest45_Uses3DegreeWindow(double input, double expected)
    {
        Assert.Equal(expected, ShapeConstraint.SnapAngleToNearest45(input), 3);
    }
}
