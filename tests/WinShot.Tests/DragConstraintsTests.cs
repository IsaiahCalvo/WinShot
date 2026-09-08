using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class DragConstraintsTests
{
    [Theory]
    [InlineData(40, 5, 40, 0)]     // mostly horizontal → keep X
    [InlineData(-40, 5, -40, 0)]   // direction survives the lock
    [InlineData(5, -40, 0, -40)]   // mostly vertical → keep Y
    [InlineData(0, 0, 0, 0)]
    public void LockAxisKeepsTheDominantComponentAndItsSign(
        double x, double y, double wantX, double wantY)
    {
        Vector locked = DragConstraints.LockAxis(new Vector(x, y));
        Assert.Equal(wantX, locked.X, 6);
        Assert.Equal(wantY, locked.Y, 6);
    }

    /// <summary>
    /// A perfect diagonal has no dominant axis. Picking X (>=) rather than flapping
    /// between the two is what stops the mark twitching when a drag sits on 45°.
    /// </summary>
    [Fact]
    public void LockAxisIsStableOnAPerfectDiagonal()
    {
        Vector locked = DragConstraints.LockAxis(new Vector(30, 30));
        Assert.Equal(new Vector(30, 0), locked);
    }

    [Theory]
    [InlineData(60, 20, 60, 60)]     // wider than tall → grow height
    [InlineData(20, 60, 60, 60)]     // taller than wide → grow width
    [InlineData(-60, 20, -60, 60)]   // dragging left keeps going left
    [InlineData(20, -60, 60, -60)]   // dragging up keeps going up
    public void SquareGrowsTheShortSideWithoutFlippingTheDrag(
        double dx, double dy, double wantDx, double wantDy)
    {
        var anchor = new Point(100, 100);
        Point squared = DragConstraints.Square(anchor, new Point(100 + dx, 100 + dy));
        Assert.Equal(100 + wantDx, squared.X, 6);
        Assert.Equal(100 + wantDy, squared.Y, 6);
        Assert.Equal(
            Math.Abs(squared.X - anchor.X),
            Math.Abs(squared.Y - anchor.Y),
            6);
    }

    /// <summary>
    /// Shift on a line or arrow snaps the BEARING and keeps the length, so the mark ends
    /// exactly on a horizontal, vertical or diagonal instead of near one.
    /// </summary>
    [Theory]
    [InlineData(100, 3)]     // nearly horizontal right
    [InlineData(-100, -4)]   // nearly horizontal left
    [InlineData(2, 100)]     // nearly vertical down
    [InlineData(70, 66)]     // nearly diagonal
    public void QuantizeBearingLandsOnAMultipleOf45(double dx, double dy)
    {
        var anchor = new Point(50, 50);
        var raw = new Point(anchor.X + dx, anchor.Y + dy);
        Point snapped = DragConstraints.QuantizeBearing(anchor, raw);

        double angle = Math.Atan2(snapped.Y - anchor.Y, snapped.X - anchor.X) * 180 / Math.PI;
        double remainder = Math.Abs(((angle % 45) + 45) % 45);
        Assert.True(remainder < 0.001 || Math.Abs(remainder - 45) < 0.001,
            $"bearing {angle:0.###}° is not a multiple of 45");

        // The drag's length is preserved, so the mark does not jump when Shift goes down.
        Assert.Equal((raw - anchor).Length, (snapped - anchor).Length, 6);
    }

    [Fact]
    public void QuantizeBearingLeavesADegenerateDragAlone()
    {
        var anchor = new Point(10, 10);
        Assert.Equal(anchor, DragConstraints.QuantizeBearing(anchor, anchor));
    }
}
