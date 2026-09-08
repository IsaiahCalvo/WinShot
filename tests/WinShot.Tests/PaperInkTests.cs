using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class PaperInkTests
{
    [Fact]
    public void Compact_DropsPointsCloserThanThreshold()
    {
        var pts = PaperInk.Compact(new[]
        {
            new Point(0, 0), new Point(0.1, 0), new Point(1, 0),
        }, minDistance: 0.35);
        Assert.Equal(2, pts.Count);
        Assert.Equal(new Point(0, 0), pts[0]);
        Assert.Equal(new Point(1, 0), pts[1]);
    }

    [Fact]
    public void Compact_KeepsTheTrueFinalSample()
    {
        // Survey compactPoints force-appends the last raw sample so the stroke ends where
        // the user released (paperAnnotationGeometry.js:82-93).
        var pts = PaperInk.Compact(new[]
        {
            new Point(0, 0), new Point(1, 0), new Point(1.1, 0),
        }, minDistance: 0.35);
        Assert.Equal(3, pts.Count);
        Assert.Equal(new Point(1.1, 0), pts[^1]);
    }

    [Fact]
    public void Outline_SelfCrossingStrokeStaysSolid()
    {
        // Survey boolean-unions the swept disks before painting them evenodd, so a
        // self-crossing stroke has no hole at the crossing (paperAnnotationGeometry.js:502-520).
        // WPF's widen output is consistently wound, so Nonzero IS that union.
        var x = PaperInk.Outline(new[]
        {
            new Point(40, 40), new Point(100, 100), new Point(100, 40), new Point(40, 100),
        }, width: 20);
        Assert.Equal(FillRule.Nonzero, x.FillRule);
        Assert.True(x.FillContains(new Point(70, 70)));

        var loop = PaperInk.Outline(new[]
        {
            new Point(100, 100), new Point(150, 100), new Point(150, 150),
            new Point(100, 150), new Point(100, 100), new Point(150, 100),
        }, width: 20);
        Assert.True(loop.FillContains(new Point(125, 100)));
    }

    [Fact]
    public void CullSlivers_DropsHairlineRemnantsButKeepsADot()
    {
        // Survey cullInkSliverPolygons: area floor min(0.4w, 0.35w^2) and a mean-thickness
        // floor of 0.141w; "a full pen DOT always survives both floors"
        // (paperAnnotationGeometry.js:1586-1615).
        var dot = PaperInk.Outline(new[] { new Point(10, 10) }, width: 8);
        Assert.False(PaperInk.IsEmpty(PaperInk.CullSlivers(dot, 8)));

        // A 0.05px-thick ribbon of "8pt" ink is debris the eraser must not leave behind.
        var sliver = new PathGeometry();
        var fig = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(
            new[] { new Point(40, 0), new Point(40, 0.05), new Point(0, 0.05) }, true));
        sliver.Figures.Add(fig);
        Assert.True(PaperInk.IsEmpty(PaperInk.CullSlivers(sliver, 8)));
    }

    [Fact]
    public void Outline_Dot_IsADiskAroundThePoint()
    {
        var g = PaperInk.Outline(new[] { new Point(10, 10) }, width: 8);
        Assert.False(PaperInk.IsEmpty(g));
        Assert.InRange(g.Bounds.Width, 7.5, 8.5);
        Assert.InRange(g.Bounds.Height, 7.5, 8.5);
        Assert.True(g.FillContains(new Point(10, 10)));
        Assert.False(g.FillContains(new Point(20, 10)));
    }

    [Fact]
    public void Outline_Segment_CoversRoundCapsBeyondTheEndpoints()
    {
        var g = PaperInk.Outline(new[] { new Point(0, 0), new Point(20, 0) }, width: 6);
        Assert.True(g.FillContains(new Point(10, 0)));
        Assert.True(g.FillContains(new Point(-1, 0))); // start cap
        Assert.True(g.FillContains(new Point(21, 0))); // end cap
        Assert.False(g.FillContains(new Point(10, 8)));
    }

    /// <summary>
    /// A non-uniform resize scales an SVG path's stroke with its geometry, so the swept pen
    /// becomes an ellipse. Stretching a 6px stroke 3x in X must give an 18x6 pen — the same
    /// outline the drag's own matrix draws — not a round 18 or a round sqrt(18*6)=10.4.
    /// </summary>
    [Fact]
    public void Outline_Aspect_SweepsAnEllipticalPen()
    {
        var pts = new[] { new Point(0, 0), new Point(60, 0) };
        var round = PaperInk.Outline(pts, width: 18);
        var flat = PaperInk.Outline(pts, width: 18, aspect: 1.0 / 3);
        Assert.Equal(18, round.Bounds.Height, 1);
        Assert.Equal(6, flat.Bounds.Height, 1);
        Assert.Equal(78, flat.Bounds.Width, 1);          // 60 + one full pen width of caps
        Assert.True(flat.FillContains(new Point(30, 2.5)));
        Assert.False(flat.FillContains(new Point(30, 4)));
    }

    [Fact]
    public void Outline_IsFilledNotStrokedCenterline()
    {
        var g = PaperInk.Outline(new[] { new Point(0, 0), new Point(40, 0), new Point(40, 20) }, 4);
        Assert.True(g.FillContains(new Point(20, 0)));
        Assert.True(g.FillContains(new Point(40, 10)));
    }

    [Fact]
    public void Subtract_CutsAHoleInInk()
    {
        var ink = PaperInk.Outline(new[] { new Point(0, 0), new Point(50, 0) }, 10);
        var cut = PaperInk.Outline(new[] { new Point(25, 0) }, 8);
        var left = PaperInk.Subtract(ink, cut)!;
        Assert.NotNull(left);
        Assert.False(left.FillContains(new Point(25, 0)));
        Assert.True(left.FillContains(new Point(5, 0)));
        Assert.True(left.FillContains(new Point(45, 0)));
    }

    [Fact]
    public void IsEmpty_JudgesTheSurvivingFiguresNotTheBoundingBox()
    {
        // A long thin remnant has a near-zero bounding-box height but is still real ink.
        var thin = PaperInk.Outline(new[] { new Point(0, 0), new Point(60, 0) }, width: 1);
        Assert.InRange(thin.Bounds.Height, 0.9, 1.1);
        Assert.False(PaperInk.IsEmpty(thin));
    }

    [Fact]
    public void Subtract_EntireStroke_IsEmpty()
    {
        var ink = PaperInk.Outline(new[] { new Point(0, 0), new Point(8, 0) }, 4);
        var cut = PaperInk.Outline(new[] { new Point(-4, 0), new Point(12, 0) }, 12);
        var left = PaperInk.Subtract(ink, cut);
        Assert.NotNull(left);
        Assert.True(PaperInk.IsEmpty(left!));
    }

    [Fact]
    public void Tolerance_KeepsFlattenedInkInsideSurveys18FacetErrorBudget()
    {
        // Survey's cap/join facet count is width-INDEPENDENT (DEFAULT_ARC_STEPS = 18,
        // paperAnnotationGeometry.js:5, :468-487), so its committed disk is always within
        // ~0.5% of the exact one. WPF's default flattening tolerance is 0.25 in ABSOLUTE canvas
        // units, so the facet count collapsed as the pen thinned - and every erase/move
        // round-trips ink through a flatten. A near-dot 1px capsule came back as 7 vertices
        // holding 84% of its area.
        foreach (double w in new[] { 1.0, 3.0, 12.0, 30.0 })
        {
            var g = PaperInk.Outline(new[] { new Point(100, 100), new Point(101, 100) }, w);
            double ideal = Math.PI * w * w / 4 + w; // disk of diameter w + a 1px-long body
            double ours = g.GetFlattenedPathGeometry(PaperInk.Tolerance, ToleranceType.Absolute).GetArea();
            Assert.InRange(ours, ideal * 0.995, ideal * 1.005);
        }
        // And the regression this replaced: WPF's own default still loses 16% at width 1.
        var thin = PaperInk.Outline(new[] { new Point(100, 100), new Point(101, 100) }, 1);
        Assert.True(thin.GetFlattenedPathGeometry().GetArea() < (Math.PI / 4 + 1) * 0.9);
    }

    [Fact]
    public void SubtractStroke_IgnoresExtraSamplesThatLieOnTheGesture()
    {
        // Survey's carve is a pure function of (geometry, WHOLE gesture polyline, radius): one
        // swept-disk record set for the gesture, subtracted once (paperAnnotationGeometry.js:
        // 2367-2380, FabricEraserCanvas.jsx:1820-1846). Inserting midpoints does not change the
        // swept region, so it must not change the ink. It did when every pointer sample ran its
        // own subtract + sliver cull: the survivors tracked sample density, not the gesture.
        var ink = PaperInk.Outline(new[] { new Point(0, 20), new Point(200, 20) }, 8);
        var gesture = Enumerable.Range(0, 25)
            .Select(i => new Point(20 + 160.0 * i / 24, 20 + 24 * Math.Sin(i * 0.7))).ToList();
        List<Point> Subdivide(List<Point> src)
        {
            var outp = new List<Point> { src[0] };
            for (int i = 1; i < src.Count; i++)
            {
                outp.Add(new Point((src[i - 1].X + src[i].X) / 2, (src[i - 1].Y + src[i].Y) / 2));
                outp.Add(src[i]);
            }
            return outp;
        }

        double Area(Geometry g) => g.GetFlattenedPathGeometry().GetArea();
        double coarse = Area(PaperInk.CullSlivers(PaperInk.SubtractStroke(ink, gesture, 16), 8));
        double dense = Area(PaperInk.CullSlivers(
            PaperInk.SubtractStroke(ink, Subdivide(Subdivide(gesture)), 16), 8));
        Assert.Equal(coarse, dense, 3);
        Assert.True(coarse < Area(ink) * 0.9, "the sweep must actually cut the stroke");
    }

    [Fact]
    public void Subtract_ReportsFailureAsNullSoNothingIsSilentlyDeleted()
    {
        // A null/throwing Geometry.Combine used to become Geometry.Empty, which EraseLive read
        // as "fully erased" and used to delete the whole annotation.
        var ink = PaperInk.Outline(new[] { new Point(0, 0), new Point(50, 0) }, 10);
        var miss = PaperInk.Outline(new[] { new Point(0, 400), new Point(50, 400) }, 10);
        var untouched = PaperInk.Subtract(ink, miss);
        Assert.NotNull(untouched);
        Assert.False(PaperInk.IsEmpty(untouched!));
        // A gesture that cannot cut anything leaves the ink whole, never empty.
        Assert.False(PaperInk.IsEmpty(PaperInk.SubtractStroke(ink, new[] { new Point(0, 400) }, 16)));
    }

    [Fact]
    public void EraserStroke_CompactsAtItsOwnRadiusRelativeDistance()
    {
        // Survey gives the eraser polyline max(MIN_VALUE, min(0.1, radius*0.1)) and passes it to
        // sweptDiskPolygonRecords (paperAnnotationGeometry.js:2367-2375). 0.35 is the INK commit
        // distance (createInkAnnotation :1539) and reusing it thinned the eraser's samples ~2:1
        // against the 0.2 live sampler, making the swept polygon differ from Survey's.
        var zig = new List<Point>();
        for (int i = 0; i <= 40; i++)
            zig.Add(new Point(i * 0.25, i % 2 == 0 ? 0 : 0.15)); // ~0.29 apart: under 0.35, over 0.1
        double Area(Geometry g) => g.GetFlattenedPathGeometry().GetArea();

        double eraser = Area(PaperInk.EraserStroke(zig, 12));
        Assert.Equal(Area(PaperInk.Outline(zig, 12, 0.1)), eraser, 6);
        // The ink constant would have dropped every wiggle, sweeping a strictly smaller region.
        Assert.True(eraser > Area(PaperInk.Outline(zig, 12)) + 0.5,
            $"eraser swept {eraser}, ink-compacted swept {Area(PaperInk.Outline(zig, 12))}");
    }

    [Fact]
    public void HighlighterWidth_StillFloorsAtEight()
    {
        Assert.Equal(8, AnnotationStyle.HighlighterWidth(3));
    }
}
