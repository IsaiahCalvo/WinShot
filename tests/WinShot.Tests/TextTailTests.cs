using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class TextTailTests
{
    private static readonly Size Pill = new(120, 32);

    [Fact]
    public void ANewPillPointsBelowItself()
    {
        Point tip = TextTail.DefaultTip(Pill);

        Assert.True(tip.Y > Pill.Height, "the default tail has to start outside its own pill");
        Assert.InRange(tip.X, 0, Pill.Width);
    }

    [Fact]
    public void ATipInsideThePillDrawsNothing()
    {
        // A tail whose tip is under its own pill is a stub sticking out of nothing.
        Assert.False(TextTail.IsVisible(Pill, new Point(60, 16)));
        Assert.Null(TextTail.Triangle(Pill, new Point(60, 16)));
    }

    [Fact]
    public void ATipOutsideThePillDrawsATriangleEndingOnIt()
    {
        var tip = new Point(40, 90);
        Point[] triangle = TextTail.Triangle(Pill, tip)!;

        Assert.Equal(3, triangle.Length);
        Assert.Equal(tip, triangle[2]);
    }

    [Fact]
    public void TheBaseSitsOnTheEdgeTheTailLeavesBy()
    {
        // Straight down: the base has to be on the bottom edge, not on a corner it was
        // assigned once.
        Point[] below = TextTail.Triangle(Pill, new Point(60, 200))!;
        Assert.Equal(Pill.Height, below[0].Y, 6);
        Assert.Equal(Pill.Height, below[1].Y, 6);

        // Straight out to the right: on the right edge.
        Point[] right = TextTail.Triangle(Pill, new Point(400, 16))!;
        Assert.Equal(Pill.Width, right[0].X, 6);
        Assert.Equal(Pill.Width, right[1].X, 6);
    }

    [Fact]
    public void TheTailSwingsRoundToWhicheverEdgeIsFacingTheTip()
    {
        Point[] up = TextTail.Triangle(Pill, new Point(60, -80))!;
        Assert.Equal(0, up[0].Y, 6);
        Assert.Equal(0, up[1].Y, 6);

        Point[] left = TextTail.Triangle(Pill, new Point(-200, 16))!;
        Assert.Equal(0, left[0].X, 6);
        Assert.Equal(0, left[1].X, 6);
    }

    [Fact]
    public void TheBaseIsAcrossTheDirectionOfTravel()
    {
        // The two base points straddle the join; the tail must not be a sliver at an angle.
        var tip = new Point(300, 300);
        Point[] t = TextTail.Triangle(Pill, tip)!;

        Vector across = t[0] - t[1];
        Vector along = tip - new Point(Pill.Width / 2, Pill.Height / 2);
        Assert.True(Math.Abs(Vector.Multiply(across, along)) < 1e-6,
            "the base should be perpendicular to the direction the tail points");
    }

    [Fact]
    public void TheBaseWidthIsBoundedAtBothEnds()
    {
        Assert.Equal(TextTail.MinBaseWidth, TextTail.BaseWidth(new Size(4, 4)));
        Assert.Equal(TextTail.MaxBaseWidth, TextTail.BaseWidth(new Size(4000, 4000)));
        Assert.InRange(TextTail.BaseWidth(Pill), TextTail.MinBaseWidth, TextTail.MaxBaseWidth);
    }

    [Fact]
    public void ATipExactlyOnThePillsCentreDrawsNothing()
    {
        // There is no direction to point in, and normalising a zero vector is how that
        // becomes a NaN triangle.
        Assert.Null(TextTail.Triangle(Pill, new Point(Pill.Width / 2, Pill.Height / 2)));
    }

    [Fact]
    public void ADegeneratePillStillProducesAFiniteTriangle()
    {
        Point[]? t = TextTail.Triangle(new Size(0, 0), new Point(50, 50));

        Assert.NotNull(t);
        Assert.All(t!, p =>
        {
            Assert.True(double.IsFinite(p.X));
            Assert.True(double.IsFinite(p.Y));
        });
    }
}
