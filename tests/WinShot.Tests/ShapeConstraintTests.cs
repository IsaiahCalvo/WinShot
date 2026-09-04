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
    public void UniformResize_EdgePillAveragesTheMovedAxisWithOne()
    {
        // Survey writes ONE magnitude to both axes (useSVGInteraction.js:2167-2171). On an
        // edge pill the untouched axis still carries scale 1, so a drag that halves the
        // height lands at (0.5 + 1)/2 = 0.75 on BOTH axes: half the drag, not the full one.
        var original = new Rect(0, 0, 20, 10);
        var unconstrained = new Rect(0, 5, 20, 5); // T dragged down, height 5 → sy 0.5
        var next = ShapeConstraint.UniformResize(original, handle: 4, unconstrained);
        Assert.Equal(15, next.Width, 3);
        Assert.Equal(7.5, next.Height, 3);
        Assert.Equal(2.5, next.X, 3);       // centred on the untouched axis
        Assert.Equal(2.5, next.Y, 3);       // anchored on the ORIGINAL bottom edge (10 - 7.5)
    }

    [Fact]
    public void UniformResize_CounterRuleTakesTheMovedAxisWholeWithNoAveraging()
    {
        // Survey's counter branch (:2174-2183) is max(|sx|,|sy|) on a corner and the moved
        // axis alone on an edge — the Shift averaging must not leak into it.
        var original = new Rect(0, 0, 20, 10);
        var edge = ShapeConstraint.UniformResize(original, handle: 4, new Rect(0, 5, 20, 5), average: false);
        Assert.Equal(10, edge.Width, 3);
        Assert.Equal(5, edge.Height, 3);

        var corner = ShapeConstraint.UniformResize(original, handle: 2, new Rect(0, 0, 40, 10), average: false);
        Assert.Equal(40, corner.Width, 3);   // max(2, 1) = 2, not the 1.5 average
        Assert.Equal(20, corner.Height, 3);
    }

    [Fact]
    public void UniformResize_MirrorsWhenTheDragCrossedTheAnchor()
    {
        // Survey keeps each axis's sign through the Shift lock (:2167-2171), so a corner
        // dragged past its anchor stays on the far side instead of bouncing back.
        var original = new Rect(0, 0, 10, 20);
        // TL handle, anchor BR = (10, 20). NewBoundsForHandle normalises a drag past the
        // anchor into a rect that STARTS at the anchor and grows away from it.
        var flipped = ShapeConstraint.UniformResize(original, handle: 0, new Rect(10, 20, 20, 20));
        Assert.Equal(10, flipped.X, 3);      // still pinned to the anchor
        Assert.Equal(20, flipped.Y, 3);
        Assert.Equal(15, flipped.Width, 3);  // sx 2, sy 1 → avg 1.5
        Assert.Equal(30, flipped.Height, 3);
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
