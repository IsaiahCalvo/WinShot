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
        Geometry g = CloudPath.ForRectangle(new Rect(10, 20, 120, 80), intensity: 2);

        // A cloud is streamed rather than built out of PathFigures (it is rebuilt on every
        // pointer move, and a big one is thousands of DependencyObjects), so the figures are
        // read back out to state the packaging rule. The round trip is what HoverGlowRing does
        // for real, and it preserves open/closed and stroked/unstroked.
        PathGeometry read = PathGeometry.CreateFromGeometry(g);
        // One un-stroked body figure that still fills, then one open figure per crown.
        PathFigure body = read.Figures[0];
        Assert.True(body.IsFilled);
        Assert.All(body.Segments, s => Assert.False(s.IsStroked));
        Assert.True(read.Figures.Count > 1);
        Assert.All(read.Figures.Skip(1), f =>
        {
            Assert.False(f.IsClosed);
            Assert.True(f.IsFilled);
            // CreateFromGeometry coalesces a crown's consecutive cubics into one
            // PolyBezierSegment, so accept either shape — what matters is cubic and stroked.
            Assert.All(f.Segments, s => Assert.True(
                s is BezierSegment { IsStroked: true } or PolyBezierSegment { IsStroked: true },
                $"crown segment is {s.GetType().Name} stroked={s.IsStroked}"));
        });
        Assert.Equal(FillRule.Nonzero, read.FillRule);

        // `bounds` is the box the PAINTED cloud fits in, not the ring the crowns grow out of:
        // the ring is inset by half a scallop plus half the pen so WPF's layout clip on the
        // Path's Width/Height slot has nothing to chop. Crowns still bulge past their own
        // ring — they just do it inside the box now.
        Assert.True(g.Bounds.Left >= 10 && g.Bounds.Top >= 20,
            $"crowns escape the box top-left: {g.Bounds}");
        Assert.True(g.Bounds.Right <= 130 && g.Bounds.Bottom <= 100,
            $"crowns escape the box bottom-right: {g.Bounds}");
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
        PathFigure crown = PathGeometry.CreateFromGeometry(g).Figures[1];
        Assert.False(ring.FillContains(crown.StartPoint));
        Assert.True(ring.Bounds.Width > g.Bounds.Width, "the ring reaches outside the ink");
    }

    /// <summary>
    /// The cloud is STREAMED now, not built out of PathFigures, and this is the test that says
    /// the switch moved nothing. A 3000x2000 Bump-1 cloud is some thousands of Bezier segments,
    /// each one a DependencyObject, and it is rebuilt on every pointer move — streaming the same
    /// path into a flat buffer measured 9.5 ms -> 1.7 ms on that box.
    ///
    /// There is exactly one place the switch could move the ink, and it is not the obvious one.
    /// The crowns are <c>RevisionCloud.Outline(kind, ring, size)</c> either way, so given the
    /// same ring the same path comes out. But a SIZED cloud measures its own ring: every sized
    /// cloud is built twice, once to read how far the crowns reach past the box and once for
    /// real on the box inset by that reach — and the measurement is
    /// <see cref="Geometry.Bounds"/>, taken off the throwaway build. So the claim under test is
    /// that a StreamGeometry's Bounds equals a PathGeometry's over the same figures, EXACTLY:
    /// a last-bit difference there moves the inset, which moves every crown, and the cloud stops
    /// being the cloud the 494 <see cref="RevisionCloudParityTests"/> fixtures approved.
    ///
    /// So the expectation is built through the ORIGINAL PathGeometry measure pass and then
    /// streamed, which makes the two sides differ in nothing but how the reach was measured.
    /// Four Bumps, four boxes (one small enough to hit the inset clamp), three pens, both walks.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(7.0)]
    [InlineData(20.0)]
    public void StreamingTheCloudDidNotMoveIt(double intensity)
    {
        foreach (Rect box in new[]
                 {
                     new Rect(0, 0, 320, 180), new Rect(10, 20, 1200, 800),
                     new Rect(-40, -15, 3000, 2000), new Rect(0, 0, 37, 29),
                 })
            foreach (double stroke in new[] { 1.0, 4.0, 12.0 })
            {
                foreach ((CloudKind kind, Geometry actual) in new[]
                         {
                             (CloudKind.Rectangle, CloudPath.ForRectangle(box, intensity, stroke)),
                             (CloudKind.Ellipse, CloudPath.ForEllipse(box, intensity, stroke)),
                         })
                {
                    Geometry expected = CloudPath.For(
                        kind, Corners(OldRing(kind, box, intensity, stroke)), intensity);
                    Assert.Equal(expected.ToString(), actual.ToString());
                    Assert.Equal(expected.Bounds, actual.Bounds);
                    Assert.False(actual.Bounds.IsEmpty,
                        $"empty cloud at bump={intensity} box={box} stroke={stroke}");
                }
            }

        static Point[] Corners(Rect b) => [b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft];

        /// <summary>FitInside as it stood, reading its reach off a PathGeometry's Bounds.</summary>
        static Rect OldRing(CloudKind kind, Rect b, double intensity, double strokeWidth)
        {
            if (b.Width <= 0 || b.Height <= 0) return b;
            Rect reach = Built(kind, Corners(b), intensity).Bounds;
            double bulge = reach.IsEmpty ? 0 : Math.Max(
                Math.Max(b.Left - reach.Left, reach.Right - b.Right),
                Math.Max(b.Top - reach.Top, reach.Bottom - b.Bottom));
            double inset = Math.Max(0, bulge) + Math.Max(0, strokeWidth) / 2;
            double max = Math.Min(b.Width, b.Height) / 2 - 0.5;
            return Rect.Inflate(b, -Math.Max(0, Math.Min(inset, max)), -Math.Max(0, Math.Min(inset, max)));
        }

        static PathGeometry Built(CloudKind kind, IReadOnlyList<Point> points, double intensity)
        {
            var g = new PathGeometry { FillRule = FillRule.Nonzero };
            List<CloudCommand> outline =
                RevisionCloud.Outline(kind, points, CloudPath.ScallopSize(intensity));
            if (outline.Count == 0) return g;
            var body = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
            for (int i = 1; i < points.Count; i++)
                body.Segments.Add(new LineSegment(points[i], isStroked: false));
            g.Figures.Add(body);
            PathFigure? crown = null;
            foreach (CloudCommand c in outline)
            {
                if (c.Verb == 'M')
                {
                    crown = new PathFigure { StartPoint = c.P1, IsClosed = false, IsFilled = true };
                    g.Figures.Add(crown);
                }
                else crown!.Segments.Add(new BezierSegment(c.P1, c.P2, c.P3, isStroked: true));
            }
            return g;
        }
    }

    /// <summary>
    /// A cloud comes out frozen. Nothing edits one in place — every call site replaces
    /// <c>Path.Data</c> outright — and a frozen geometry skips the change-notification plumbing
    /// on a shape that is rebuilt sixty times a second during a drag.
    /// </summary>
    [Fact]
    public void ACloudComesOutFrozen()
    {
        Assert.True(CloudPath.ForRectangle(new Rect(0, 0, 200, 140)).IsFrozen);
        Assert.True(CloudPath.ForEllipse(new Rect(0, 0, 200, 140)).IsFrozen);
        Assert.True(CloudPath.ForPolygon([new(0, 0), new(100, 0), new(60, 90)]).IsFrozen);
        Assert.True(CloudPath.ForPolyline([new(0, 0), new(100, 0), new(60, 90)]).IsFrozen);
        // The degenerate early-out too: a caller that assigns it must not get a live object
        // from one branch and a frozen one from the other.
        Assert.True(CloudPath.For(CloudKind.Polygon, [], 2).IsFrozen);
    }
}
