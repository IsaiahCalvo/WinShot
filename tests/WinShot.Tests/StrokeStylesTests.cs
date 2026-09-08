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

    [Theory]
    [InlineData(null, StrokeRender.Clean)]
    [InlineData("nonsense", StrokeRender.Clean)]
    [InlineData("Sketchy", StrokeRender.Sketchy)]
    public void AnUnknownRenderNameReadsAsClean(string? name, StrokeRender expected) =>
        Assert.Equal(expected, StrokeStyles.ParseRender(name));

    // ------------------------------------------------------------------ sketchy

    [Fact]
    public void ASketchySegmentStartsAndEndsExactlyWhereItWasAsked()
    {
        // The wobble tapers into the endpoints. A shape whose corners drifted would
        // stop being a shape.
        var from = new Point(10, 20);
        var to = new Point(200, 60);
        var pts = StrokeStyles.SketchSegment(from, to, seed: 7);

        Assert.Equal(from, pts[0]);
        Assert.Equal(to, pts[^1]);
    }

    [Fact]
    public void ASketchySegmentWandersInTheMiddleButNotFar()
    {
        var from = new Point(0, 0);
        var to = new Point(300, 0);
        var pts = StrokeStyles.SketchSegment(from, to, seed: 11);

        Assert.True(pts.Count > 2, "a straight run should be resampled into waypoints");
        double worst = pts.Max(p => Math.Abs(p.Y));
        Assert.True(worst > 0, "a sketchy line has to actually leave the ruled path");
        Assert.True(worst <= StrokeStyles.SketchAmplitude + 1e-9,
            $"a sketchy line wandered {worst:0.###}px, past its own amplitude");
    }

    [Fact]
    public void TheSameSeedDrawsTheSameLine()
    {
        // The seed lives on the annotation precisely so a mark does not re-scribble
        // itself on every redraw.
        var a = StrokeStyles.SketchSegment(new Point(0, 0), new Point(120, 90), seed: 42);
        var b = StrokeStyles.SketchSegment(new Point(0, 0), new Point(120, 90), seed: 42);
        var c = StrokeStyles.SketchSegment(new Point(0, 0), new Point(120, 90), seed: 43);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ADegenerateSegmentIsJustItsTwoPoints()
    {
        var p = new Point(5, 5);
        Assert.Equal(new List<Point> { p, p }, StrokeStyles.SketchSegment(p, p, seed: 1));
    }

    [Fact]
    public void ASketchyRectangleClosesOnItself()
    {
        var corners = StrokeStyles.RectCorners(new Rect(0, 0, 100, 60));
        var outline = StrokeStyles.SketchPolygon(corners, seed: 3);

        // Every corner survives as a real point, and the run does not repeat the
        // previous edge's end.
        foreach (Point corner in corners)
            Assert.Contains(outline, p => (p - corner).Length < 1e-9);
        Assert.Equal(outline.Count, outline.Distinct().Count());
    }

    [Fact]
    public void EachEdgeGetsItsOwnSeed()
    {
        // Otherwise every edge of a square would wobble identically, which reads as a
        // pattern rather than a hand.
        var outline = StrokeStyles.SketchPolygon(StrokeStyles.RectCorners(new Rect(0, 0, 100, 100)), seed: 5);
        var top = outline.Where(p => p.Y < 10 && p.X > 1 && p.X < 99).Select(p => p.Y).ToList();
        var bottom = outline.Where(p => p.Y > 90 && p.X > 1 && p.X < 99).Select(p => 100 - p.Y).ToList();

        Assert.NotEmpty(top);
        Assert.NotEmpty(bottom);
        Assert.NotEqual(top, bottom);
    }

    [Fact]
    public void EllipsePointsCloseAndStayInsideTheRect()
    {
        var r = new Rect(10, 20, 100, 40);
        var pts = StrokeStyles.EllipsePoints(r, samples: 32);

        Assert.Equal(32, pts.Length);
        foreach (Point p in pts)
        {
            Assert.InRange(p.X, r.Left - 1e-9, r.Right + 1e-9);
            Assert.InRange(p.Y, r.Top - 1e-9, r.Bottom + 1e-9);
        }
    }

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
