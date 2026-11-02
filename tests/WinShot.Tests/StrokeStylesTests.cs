using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class StrokeStylesTests
{
    // ------------------------------------------------------------------- dashes

    [Fact]
    public void SolidHasNoDashArrayAtAll()
    {
        // Not an empty collection — WPF throws on one. Null is how "solid" is spelled.
        Assert.Null(StrokeStyles.DashArray(StrokeDash.Solid));
    }

    [Theory]
    [InlineData(StrokeDash.Dashed)]
    [InlineData(StrokeDash.Dotted)]
    public void DashPatternsAreFrozenSoOneInstanceCanBackEveryMark(StrokeDash dash)
    {
        DoubleCollection? c = StrokeStyles.DashArray(dash);
        Assert.NotNull(c);
        Assert.True(c!.IsFrozen);
        Assert.NotEmpty(c);
    }

    [Fact]
    public void ADotIsAZeroLengthDashWithRoundCaps()
    {
        // A dot with a length is just a short dash, which is Dashed again.
        DoubleCollection c = StrokeStyles.DashArray(StrokeDash.Dotted)!;
        Assert.True(c[0] < 0.01);
        Assert.Equal(PenLineCap.Round, StrokeStyles.CapFor(StrokeDash.Dotted));
        Assert.NotEqual(PenLineCap.Round, StrokeStyles.CapFor(StrokeDash.Dashed));
    }

    [Theory]
    [InlineData(null, StrokeDash.Solid)]
    [InlineData("", StrokeDash.Solid)]
    [InlineData("nonsense", StrokeDash.Solid)]
    [InlineData("Dotted", StrokeDash.Dotted)]
    public void AnUnknownDashNameReadsAsSolid(string? name, StrokeDash expected) =>
        Assert.Equal(expected, StrokeStyles.ParseDash(name));

    // -------------------------------------------------------------- simplifying

    [Fact]
    public void SimplifyKeepsBothEnds()
    {
        var pts = new List<Point> { new(0, 0), new(1, 0.1), new(2, 0), new(3, 0.05), new(4, 0) };
        var simple = StrokeStyles.Simplify(pts, epsilon: 1);

        Assert.Equal(pts[0], simple[0]);
        Assert.Equal(pts[^1], simple[^1]);
    }

    [Fact]
    public void SimplifyCollapsesANearlyStraightRun()
    {
        var pts = new List<Point>();
        for (int i = 0; i <= 100; i++) pts.Add(new Point(i, i % 2 == 0 ? 0 : 0.3));

        var simple = StrokeStyles.Simplify(pts, epsilon: 1.2);

        Assert.Equal(2, simple.Count);
    }

    [Fact]
    public void SimplifyKeepsADeliberateCorner()
    {
        // The whole point: the pointer's noise goes, the change of direction stays.
        var pts = new List<Point>();
        for (int i = 0; i <= 50; i++) pts.Add(new Point(i, 0));
        for (int i = 1; i <= 50; i++) pts.Add(new Point(50, i));

        var simple = StrokeStyles.Simplify(pts, epsilon: 1.2);

        Assert.Equal(3, simple.Count);
        Assert.Equal(new Point(50, 0), simple[1]);
    }

    [Fact]
    public void SimplifyLeavesShortRunsAndZeroEpsilonAlone()
    {
        var two = new List<Point> { new(0, 0), new(9, 9) };
        Assert.Equal(two, StrokeStyles.Simplify(two, epsilon: 5));

        var many = new List<Point> { new(0, 0), new(1, 1), new(2, 0) };
        Assert.Equal(many, StrokeStyles.Simplify(many, epsilon: 0));
    }

    [Fact]
    public void SimplifyHandlesAStrokeThatEndsWhereItStarted()
    {
        // A closed loop makes the first RDP span degenerate: both anchors are the same
        // point, so there is no line to measure perpendicular distance from.
        var pts = new List<Point> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) };

        var simple = StrokeStyles.Simplify(pts, epsilon: 1);

        Assert.Equal(pts.Count, simple.Count);
    }

    [Fact]
    public void SimplifySurvivesALongStrokeWithoutRecursingPerPoint()
    {
        // A real scribble is thousands of points, and the worst case for a recursive RDP
        // is one stack frame per point.
        var pts = new List<Point>();
        var rng = new Random(1);
        for (int i = 0; i < 20_000; i++) pts.Add(new Point(i, rng.NextDouble() * 100));

        var simple = StrokeStyles.Simplify(pts, epsilon: 1.2);

        Assert.True(simple.Count >= 2);
        Assert.True(simple.Count <= pts.Count);
    }
}
