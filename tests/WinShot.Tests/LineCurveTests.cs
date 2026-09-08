using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class LineCurveTests
{
    [Fact]
    public void ControlThroughMid_MakesCurvePassThroughTheHandleAtHalfT()
    {
        var start = new Point(0, 0);
        var end = new Point(100, 0);
        var mid = new Point(50, 40);
        Point c = LineCurve.ControlThroughMid(start, end, mid);
        // Q(t=0.5) = 0.25 start + 0.5 C + 0.25 end
        Point atHalf = new(
            0.25 * start.X + 0.5 * c.X + 0.25 * end.X,
            0.25 * start.Y + 0.5 * c.Y + 0.25 * end.Y);
        Assert.InRange(atHalf.X, mid.X - 0.01, mid.X + 0.01);
        Assert.InRange(atHalf.Y, mid.Y - 0.01, mid.Y + 0.01);
    }

    [Fact]
    public void ShouldSnapToLinear_InsideTenPixels()
    {
        var start = new Point(0, 0);
        var end = new Point(100, 0);
        Assert.True(LineCurve.ShouldSnapToLinear(new Point(50, 10), start, end));
        Assert.False(LineCurve.ShouldSnapToLinear(new Point(50, 11), start, end));
    }

    [Fact]
    public void IsCurved_UsesOnePixelHysteresis()
    {
        var start = new Point(0, 0);
        var end = new Point(80, 0);
        Assert.False(LineCurve.IsCurved(new Point(40, 1), start, end));
        Assert.True(LineCurve.IsCurved(new Point(40, 1.01), start, end));
        Assert.False(LineCurve.IsCurved(null, start, end));
    }

    [Fact]
    public void Stroke_StraightUsesLineSegment()
    {
        var g = LineCurve.Stroke(new Point(0, 0), new Point(10, 0), mid: null);
        Assert.Single(g.Figures);
        Assert.IsType<LineSegment>(g.Figures[0].Segments[0]);
    }

    [Fact]
    public void Stroke_CurvedUsesQuadratic()
    {
        var g = LineCurve.Stroke(new Point(0, 0), new Point(10, 0), new Point(5, 8));
        Assert.IsType<QuadraticBezierSegment>(g.Figures[0].Segments[0]);
    }

    [Fact]
    public void EndAngle_FollowsControlToTip()
    {
        var start = new Point(0, 0);
        var end = new Point(10, 0);
        var mid = new Point(5, 10);
        double angle = LineCurve.EndAngleRadians(start, end, mid);
        // Control is above the baseline, so the end tangent points down-right-ish / along +X after C below? 
        // C = (5, 20). Tangent end-C = (5, -20) → atan2(-20, 5) is negative (down from C to end).
        Assert.True(angle < 0);
    }

    [Fact]
    public void DegenerateSegment_DoesNotThrow()
    {
        var p = new Point(3, 3);
        Assert.Equal(0, LineCurve.DistanceToSegment(p, p, p));
        Assert.False(LineCurve.IsCurved(p, p, p));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(50)]
    public void ClampSize_SurveyWidthRange(int size)
    {
        Assert.Equal(size, AnnotationStyle.ClampSize(size));
    }

    [Fact]
    public void HighlighterWidth_FloorsAtEight()
    {
        Assert.Equal(8, AnnotationStyle.HighlighterWidth(1));
        Assert.Equal(8, AnnotationStyle.HighlighterWidth(8));
        Assert.Equal(20, AnnotationStyle.HighlighterWidth(20));
        Assert.Equal(50, AnnotationStyle.HighlighterWidth(50));
    }
}
