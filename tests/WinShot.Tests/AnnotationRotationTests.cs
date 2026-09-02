using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class AnnotationRotationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(180, 180)]
    [InlineData(181, -179)]
    [InlineData(-180, 180)]
    [InlineData(360, 0)]
    [InlineData(370, 10)]
    [InlineData(-370, -10)]
    [InlineData(720, 0)]
    public void Normalize_WrapsIntoAHalfOpenTurn(double input, double expected)
    {
        Assert.Equal(expected, AnnotationRotation.Normalize(input), 6);
    }

    [Fact]
    public void Normalize_SurvivesNonFiniteInput()
    {
        Assert.Equal(0, AnnotationRotation.Normalize(double.NaN));
        Assert.Equal(0, AnnotationRotation.Normalize(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(44, 45)]        // inside the threshold: latches on
    [InlineData(46, 45)]
    [InlineData(14, 15)]        // the finer 15° stops snap too
    [InlineData(31, 30)]
    [InlineData(2, 0)]
    [InlineData(-2, 0)]
    [InlineData(89, 90)]
    public void SnapToNearest_LatchesInsideTheThreshold(double input, double expected)
    {
        Assert.Equal(expected, AnnotationRotation.SnapToNearest(input), 6);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(-8)]
    [InlineData(37)]
    [InlineData(100)]
    public void SnapToNearest_LeavesFreeAnglesAlone(double input)
    {
        // The soft snap must not fight a deliberate off-angle.
        Assert.Equal(input, AnnotationRotation.SnapToNearest(input), 6);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(22, 15)]     // below the 22.5 midpoint, so it steps down
    [InlineData(23, 30)]
    [InlineData(-38, -45)]
    [InlineData(100, 105)]
    public void Quantize_AlwaysStepsForShiftDrag(double input, double expected)
    {
        Assert.Equal(expected, AnnotationRotation.Quantize(input), 6);
    }

    [Theory]
    // Straight up from the pivot is zero, then clockwise positive.
    [InlineData(0, -10, 0)]
    [InlineData(10, 0, 90)]
    [InlineData(0, 10, 180)]
    [InlineData(-10, 0, -90)]
    public void AngleFromPointer_MeasuresClockwiseFromStraightUp(double dx, double dy, double expected)
    {
        var pivot = new Point(100, 100);
        var pointer = new Point(100 + dx, 100 + dy);
        Assert.Equal(expected, AnnotationRotation.AngleFromPointer(pivot, pointer), 6);
    }

    [Fact]
    public void AngleFromPointer_IsZeroWhenThePointerSitsOnThePivot()
    {
        var p = new Point(50, 50);
        Assert.Equal(0, AnnotationRotation.AngleFromPointer(p, p));
    }

    [Fact]
    public void ResolveDrag_TakesTheAbsoluteBearingWithNoGrabOffset()
    {
        // Survey rotates to the bearing from the shape centre to the pointer with no grab
        // offset (useSVGInteraction.js:2350-2352): the top faces the cursor immediately.
        double result = AnnotationRotation.ResolveDrag(pointerAngle: 40, shift: false);
        Assert.Equal(40, result, 6);
    }

    [Fact]
    public void ResolveDrag_SoftSnapsNearAnIncrement()
    {
        double result = AnnotationRotation.ResolveDrag(pointerAngle: 44, shift: false);
        Assert.Equal(45, result, 6);

        double fine = AnnotationRotation.ResolveDrag(pointerAngle: 16, shift: false);
        Assert.Equal(15, fine, 6);
    }

    [Fact]
    public void ResolveDrag_WithShiftAlwaysLandsOnAnIncrement()
    {
        double result = AnnotationRotation.ResolveDrag(pointerAngle: 38, shift: true);
        Assert.Equal(45, result, 6);

        double back = AnnotationRotation.ResolveDrag(pointerAngle: 7, shift: true);
        Assert.Equal(0, back, 6);
    }

    [Theory]
    // Survey counters: pointer angle is the bearing from the badge centre, 0 = east.
    [InlineData(10, 0, 0)]
    [InlineData(0, 10, 90)]
    [InlineData(-10, 0, 180)]
    [InlineData(0, -10, -90)]
    public void PointerAngleFromCursor_IsEastBasedLikeSurveysCounter(double dx, double dy, double expected)
    {
        var c = new Point(100, 100);
        Assert.Equal(expected, AnnotationRotation.PointerAngleFromCursor(c, new Point(100 + dx, 100 + dy)), 6);
    }

    [Fact]
    public void PointerAngleFromCursor_NeverSnaps()
    {
        // Survey's counter tip handle applies no snapping and reads no modifier
        // (SVGAnnotationLayer.jsx:5183): the nub follows the cursor continuously.
        var c = new Point(100, 100);
        double rad = 16 * Math.PI / 180;
        var cursor = new Point(100 + Math.Cos(rad) * 50, 100 + Math.Sin(rad) * 50);
        Assert.Equal(16, AnnotationRotation.PointerAngleFromCursor(c, cursor), 6);
    }

    [Fact]
    public void ResolveDrag_WrapsPastHalfATurn()
    {
        double result = AnnotationRotation.ResolveDrag(pointerAngle: 200, shift: false);
        Assert.Equal(-160, result, 6);
    }
}
