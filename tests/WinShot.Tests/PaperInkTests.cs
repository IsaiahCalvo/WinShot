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
        var left = PaperInk.Subtract(ink, cut);
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
        Assert.True(PaperInk.IsEmpty(left));
    }

    [Fact]
    public void HighlighterWidth_StillFloorsAtEight()
    {
        Assert.Equal(8, AnnotationStyle.HighlighterWidth(3));
    }
}
