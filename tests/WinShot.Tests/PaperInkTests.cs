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
