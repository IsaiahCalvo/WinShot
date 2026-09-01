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
    [InlineData(43, 45)]        // exactly 2 off
    [InlineData(2, 0)]
    [InlineData(-2, 0)]
    [InlineData(89, 90)]
    public void SnapToNearest45_LatchesInsideTheThreshold(double input, double expected)
    {
        Assert.Equal(expected, AnnotationRotation.SnapToNearest45(input), 6);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-12)]
    [InlineData(60)]
    [InlineData(100)]
    public void SnapToNearest45_LeavesFreeAnglesAlone(double input)
    {
        // The soft snap must not fight a deliberate off-angle.
        Assert.Equal(input, AnnotationRotation.SnapToNearest45(input), 6);
    }

    [Theory]
    [InlineData(30, 45)]
    [InlineData(20, 0)]      // below the 22.5 midpoint, so it steps down
    [InlineData(10, 0)]
    [InlineData(-30, -45)]
    [InlineData(100, 90)]
    public void QuantizeTo45_AlwaysStepsForShiftDrag(double input, double expected)
    {
        Assert.Equal(expected, AnnotationRotation.QuantizeTo45(input), 6);
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
    public void ResolveDrag_KeepsTheGrabOffsetSoTheMarkDoesNotJump()
    {
        // Grabbed the handle at a bearing of 10° while the shape sat at 0°.
        // Moving the pointer to 40° should advance the shape by 30°, not to 40°.
        double result = AnnotationRotation.ResolveDrag(
            startAngle: 0, grabOffset: 10, pointerAngle: 40, shift: false);
        Assert.Equal(30, result, 6);
    }

    [Fact]
    public void ResolveDrag_SoftSnapsNearAMultipleOf45()
    {
        double result = AnnotationRotation.ResolveDrag(
            startAngle: 0, grabOffset: 0, pointerAngle: 44, shift: false);
        Assert.Equal(45, result, 6);
    }

    [Fact]
    public void ResolveDrag_WithShiftAlwaysLandsOnAMultipleOf45()
    {
        double result = AnnotationRotation.ResolveDrag(
            startAngle: 0, grabOffset: 0, pointerAngle: 30, shift: true);
        Assert.Equal(45, result, 6);

        double back = AnnotationRotation.ResolveDrag(
            startAngle: 0, grabOffset: 0, pointerAngle: 10, shift: true);
        Assert.Equal(0, back, 6);
    }

    [Fact]
    public void ResolveDrag_WrapsPastHalfATurn()
    {
        double result = AnnotationRotation.ResolveDrag(
            startAngle: 170, grabOffset: 0, pointerAngle: 30, shift: false);
        Assert.Equal(-160, result, 6);
    }
}
