using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The WPF side of the cloud. The crown geometry itself is diffed against Survey's approved
/// reference in <see cref="RevisionCloudParityTests"/>; this covers how that geometry is
/// packaged for a <see cref="System.Windows.Shapes.Path"/> and the selection-chrome numbers.
/// </summary>
public class CloudPathTests
{
    [Fact]
    public void ForRectangle_FillsTheScallopedRegionAndStrokesOnlyTheCrowns()
    {
        var g = CloudPath.ForRectangle(new Rect(10, 20, 120, 80), intensity: 2);

        // One un-stroked body figure that still fills, then one open figure per crown.
        PathFigure body = g.Figures[0];
        Assert.True(body.IsFilled);
        Assert.All(body.Segments, s => Assert.False(s.IsStroked));
        Assert.True(g.Figures.Count > 1);
        Assert.All(g.Figures.Skip(1), f =>
        {
            Assert.False(f.IsClosed);
            Assert.True(f.IsFilled);
            Assert.All(f.Segments, s => Assert.True(s is BezierSegment { IsStroked: true }));
        });
        Assert.Equal(FillRule.Nonzero, g.FillRule);

        // The fill reaches the crown apexes, past the box the cloud was built from.
        Assert.True(g.Bounds.Left < 10 && g.Bounds.Top < 20);
        Assert.True(g.Bounds.Right > 130 && g.Bounds.Bottom > 100);
    }

    [Fact]
    public void ForRectangle_TinyRect_StillProducesGeometry()
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 4, 4), intensity: 1);
        Assert.False(g.Bounds.IsEmpty);
        Assert.True(g.Bounds.Width > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(20)]
    public void ForRectangle_AnyIntensity_StaysFinite(double intensity)
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 80, 40), intensity);
        Assert.False(double.IsNaN(g.Bounds.X));
        Assert.False(double.IsNaN(g.Bounds.Width));
    }

    /// <summary>
    /// Survey's ladder: 14 units per Bump, capped at the studio's 80 maximum, and the line
    /// width never enters into it (the old Acrobat stroke floor is gone).
    /// </summary>
    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 28)]
    [InlineData(5, 70)]
    [InlineData(6, 80)]
    [InlineData(20, 80)]
    public void ScallopSize_FollowsTheBumpLadder(double intensity, double expected) =>
        Assert.Equal(expected, CloudPath.ScallopSize(intensity));

    [Fact]
    public void SelectionFrame_ClearsTheCrownsByOneAndAHalfStrokeWidths()
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 300, 200));
        Rect frame = CloudPath.SelectionFrame(g, strokeWidth: 4);
        Assert.Equal(g.Bounds.Left - 6, frame.Left, 6);
        Assert.Equal(g.Bounds.Top - 6, frame.Top, 6);
        Assert.Equal(g.Bounds.Width + 12, frame.Width, 6);
        Assert.Equal(g.Bounds.Height + 12, frame.Height, 6);
    }

    /// <summary>Always eight grabbers, and every one of them sits ON the frame.</summary>
    [Fact]
    public void SelectionAnchors_AreEightPointsOnTheFrame()
    {
        var frame = new Rect(10, 20, 100, 60);
        Point[] anchors = CloudPath.SelectionAnchors(frame);
        Assert.Equal(8, anchors.Length);
        Assert.Equal(8, anchors.Distinct().Count());
        Assert.All(anchors, p => Assert.True(
            (p.X == frame.Left || p.X == frame.Right || p.X == frame.Left + frame.Width / 2) &&
            (p.Y == frame.Top || p.Y == frame.Bottom || p.Y == frame.Top + frame.Height / 2),
            $"{p} is off the frame"));
        Assert.Equal(new Point(10, 20), anchors[0]);
        Assert.Equal(new Point(110, 80), anchors[4]);
    }

    /// <summary>
    /// The glow is a ring, not a bed: the ink band is knocked out of it, so the cloud's own
    /// colour survives a translucent stroke instead of reading purple.
    /// </summary>
    [Fact]
    public void HoverGlowRing_KnocksTheInkBandOut()
    {
        var g = CloudPath.ForRectangle(new Rect(0, 0, 200, 140));
        Assert.Equal(5.7, CloudPath.HoverGlowWidth(2), 6);
        Geometry ring = CloudPath.HoverGlowRing(g, strokeWidth: 2);
        // A point dead on a crown is inside the ink band, so it is NOT in the ring.
        PathFigure crown = g.Figures[1];
        Assert.False(ring.FillContains(crown.StartPoint));
        Assert.True(ring.Bounds.Width > g.Bounds.Width, "the ring reaches outside the ink");
    }
}
