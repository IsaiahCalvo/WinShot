using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Survey's polygon / polyline drafting rules (src/utils/polyDraft.js), ported to
/// <see cref="PolyDraft"/>. Pure — no window, so these run in a full suite.
///
/// The rule the whole gesture hangs on is that finishing is EXPLICIT. A click adds a vertex; it
/// only ends the shape when it lands on the first point's magnet. A draft that is too short to
/// commit stays ALIVE rather than being silently thrown away, because the alternative is losing
/// the corners the user already placed to one mis-click.
/// </summary>
public class PolyDraftTests
{
    private static PolyDraft Polygon(params (double x, double y)[] pts) => Build(AnnotationData.TypePolygon, pts);
    private static PolyDraft Polyline(params (double x, double y)[] pts) => Build(AnnotationData.TypePolyline, pts);

    private static PolyDraft Build(string tool, (double x, double y)[] pts)
    {
        var draft = PolyDraft.Create(tool, new Point(pts[0].x, pts[0].y));
        for (int i = 1; i < pts.Length; i++) draft = draft.AddPoint(new Point(pts[i].x, pts[i].y));
        return draft;
    }

    [Fact]
    public void ACreatedDraftHoldsOnePointAndPreviewsItself()
    {
        var draft = PolyDraft.Create(AnnotationData.TypePolygon, new Point(10, 20));
        Assert.Single(draft.Points);
        Assert.Equal(new Point(10, 20), draft.Preview);
        Assert.False(draft.SnapToFirst);
    }

    [Fact]
    public void AddingAPointNeverMutatesTheDraftItCameFrom()
    {
        var one = PolyDraft.Create(AnnotationData.TypePolygon, new Point(0, 0));
        var two = one.AddPoint(new Point(10, 0));
        Assert.Single(one.Points);
        Assert.Equal(2, two.Points.Count);
    }

    /// <summary>A polygon needs three corners — two points can only ever be a line. A polyline
    /// needs two.</summary>
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, true, true)]
    public void MinimumPointCountsMatchTheType(int count, bool polygonCanFinish, bool polylineCanFinish)
    {
        var pts = Enumerable.Range(0, count).Select(i => ((double)(i * 10), 0d)).ToArray();
        Assert.Equal(polygonCanFinish, Build(AnnotationData.TypePolygon, pts).CanFinish);
        Assert.Equal(polylineCanFinish, Build(AnnotationData.TypePolyline, pts).CanFinish);
    }

    [Fact]
    public void ClosingAlwaysNeedsThreeCornersEvenForAPolyline()
    {
        Assert.False(Polyline((0, 0), (10, 0)).CanClose);
        Assert.True(Polyline((0, 0), (10, 0), (10, 10)).CanClose);
    }

    /// <summary>
    /// 'close' on EITHER tool produces a polygon — this is how a polyline becomes one — while
    /// 'finish' keeps the tool's own type.
    /// </summary>
    [Fact]
    public void CloseMakesAPolygonOutOfEitherTool()
    {
        Assert.Equal(AnnotationData.TypePolygon, Polyline((0, 0), (10, 0), (10, 10)).Resolve(close: true).FinalType);
        Assert.Equal(AnnotationData.TypePolyline, Polyline((0, 0), (10, 0), (10, 10)).Resolve(close: false).FinalType);
        Assert.Equal(AnnotationData.TypePolygon, Polygon((0, 0), (10, 0), (10, 10)).Resolve(close: false).FinalType);
    }

    /// <summary>
    /// Too few points is NOT a silent discard: Resolve reports failure so the caller keeps the
    /// draft and the user simply keeps clicking.
    /// </summary>
    [Fact]
    public void ATooShortDraftReportsFailureRatherThanCommittingSomething()
    {
        Assert.False(Polygon((0, 0), (10, 0)).Resolve(close: false).Ok);
        Assert.False(Polyline((0, 0)).Resolve(close: false).Ok);
        Assert.False(Polyline((0, 0), (10, 0)).Resolve(close: true).Ok);   // 2 points cannot close
    }

    /// <summary>
    /// A double-click (or a jittery one) leaves a duplicate trailing vertex. Compaction drops
    /// it so a three-click polygon is not rejected for a degenerate final edge.
    /// </summary>
    [Fact]
    public void ADuplicateTrailingVertexIsCompactedAway()
    {
        var draft = Polygon((0, 0), (40, 0), (40, 40)).AddPoint(new Point(41, 40));
        var finish = draft.Resolve(close: false);
        Assert.True(finish.Ok);
        Assert.Equal(3, finish.Points.Count);   // the 1px-away 4th point is dropped
    }

    [Fact]
    public void CompactionStopsAtTheFirstRealEdge()
    {
        var pts = new[] { new Point(0, 0), new Point(40, 0), new Point(41, 0), new Point(41.5, 0) };
        var kept = PolyDraft.Compact(pts);
        Assert.Equal(2, kept.Count);   // the two sub-epsilon trailing points go, (40,0) stays
        Assert.Equal(new Point(40, 0), kept[1]);
    }

    /// <summary>
    /// The first vertex is a magnet only once closing is LEGAL. On a two-point draft the same
    /// cursor position must place a third vertex, not close a shape that cannot exist.
    /// </summary>
    [Fact]
    public void TheFirstPointIsOnlyMagneticOnceClosingIsLegal()
    {
        Assert.False(Polyline((0, 0), (40, 0)).IsNearFirstPoint(new Point(2, 2), radius: 14));
        Assert.True(Polyline((0, 0), (40, 0), (40, 40)).IsNearFirstPoint(new Point(2, 2), radius: 14));
    }

    [Fact]
    public void OutsideTheRadiusTheFirstPointIsNotAMagnet() =>
        Assert.False(Polygon((0, 0), (40, 0), (40, 40)).IsNearFirstPoint(new Point(30, 30), radius: 14));

    /// <summary>
    /// Inside the magnet the PREVIEW jumps onto the first vertex, so the user sees the shape
    /// close before committing to the click.
    /// </summary>
    [Fact]
    public void ThePreviewSnapsOntoTheFirstVertexInsideTheMagnet()
    {
        var draft = Polygon((0, 0), (40, 0), (40, 40)).UpdatePreview(new Point(3, 4), snapRadius: 14);
        Assert.True(draft.SnapToFirst);
        Assert.Equal(new Point(0, 0), draft.Preview);
    }

    [Fact]
    public void OutsideTheMagnetThePreviewFollowsTheCursor()
    {
        var draft = Polygon((0, 0), (40, 0), (40, 40)).UpdatePreview(new Point(30, 25), snapRadius: 14);
        Assert.False(draft.SnapToFirst);
        Assert.Equal(new Point(30, 25), draft.Preview);
    }

    // ------------------------------------------------------------------ Shift: 45-degree snap

    [Theory]
    [InlineData(100, 3, 100, 0)]        // nearly horizontal -> horizontal
    [InlineData(3, 100, 0, 100)]        // nearly vertical   -> vertical
    [InlineData(100, 90, 95, 95)]       // near the diagonal -> 45 degrees, length preserved
    public void ShiftSnapsASegmentToTheNearest45DegreeRay(
        double dx, double dy, double expectX, double expectY)
    {
        var anchor = new Point(0, 0);
        Point snapped = PolyDraft.SnapSegmentAngle(anchor, new Point(dx, dy));
        double length = Math.Sqrt(dx * dx + dy * dy);
        // The RAY is what is snapped; the length is preserved exactly.
        Assert.Equal(length, (snapped - anchor).Length, 6);
        double expectAngle = Math.Atan2(expectY, expectX);
        Assert.Equal(expectAngle, Math.Atan2(snapped.Y, snapped.X), 6);
    }

    [Fact]
    public void SnappingAZeroLengthSegmentStaysPutInsteadOfDividingByZero() =>
        Assert.Equal(new Point(5, 5), PolyDraft.SnapSegmentAngle(new Point(5, 5), new Point(5, 5)));

    [Fact]
    public void ShiftAppliesToTheNewSegmentOnly()
    {
        var draft = Polygon((0, 0), (40, 13)).AddPoint(new Point(80, 14), shift: true);
        Assert.Equal(new Point(40, 13), draft.Points[1]);     // the earlier vertex is untouched
        Assert.Equal(13, draft.Points[2].Y, 6);              // the new one snapped horizontal
    }

    // ------------------------------------------------------------------ finish controls

    /// <summary>
    /// Two checkmarks, Drawboard-style: the FIRST vertex closes the shape, the LATEST one
    /// finishes it where it stands.
    /// </summary>
    [Fact]
    public void TheFinishControlsSitOnTheFirstAndLatestVertices()
    {
        var (first, last) = Polygon((0, 0), (40, 0), (40, 40)).FinishControls;
        Assert.Equal(new Point(0, 0), first);
        Assert.Equal(new Point(40, 40), last);
    }

    // ------------------------------------------------------------------ vertex editing

    [Fact]
    public void MovingOneVertexLeavesEveryOtherByteIdentical()
    {
        Point[] pts = [new(0, 0), new(40, 0), new(40, 40)];
        Point[] moved = PolyDraft.MoveVertex(pts, 1, new Point(17.3333333, 4.6666667));
        Assert.Equal(pts[0], moved[0]);
        Assert.Equal(pts[2], moved[2]);
        Assert.Equal(new Point(17.3333333, 4.6666667), moved[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnOutOfRangeVertexIndexChangesNothing(int index)
    {
        Point[] pts = [new(0, 0), new(40, 0), new(40, 40)];
        Assert.Equal(pts, PolyDraft.MoveVertex(pts, index, new Point(99, 99)));
    }

    // ------------------------------------------------------------------ the cloud, on a ring

    /// <summary>
    /// The point of Polygon, beyond one more shape: the revision cloud stops being
    /// rectangle-only. The crown engine has always taken an arbitrary ring — it just had no
    /// caller handing it one. A polygon cloud must therefore follow the VERTICES, not the
    /// bounding box, which is measurable: a triangle's cloud has no ink out at the box corner
    /// the triangle does not reach.
    /// </summary>
    [Fact]
    public void APolygonCloudFollowsTheRingNotTheBoundingBox()
    {
        Point[] triangle = [new(0, 0), new(200, 0), new(0, 200)];
        var cloud = CloudPath.ForPolygon(triangle);
        Assert.False(cloud.IsEmpty());

        // The far corner of the bounding box (200,200) is outside the triangle. A box-derived
        // cloud would put crowns there; a ring-derived one leaves it empty.
        Assert.False(cloud.FillContains(new Point(190, 190)));
        Assert.True(cloud.FillContains(new Point(40, 40)));
    }

    /// <summary>An open run gets no body figure, so a polyline cloud encloses nothing — there
    /// is no phantom chord from the last point back to the first.</summary>
    [Fact]
    public void APolylineCloudEnclosesNothing()
    {
        Point[] run = [new(0, 0), new(200, 0), new(200, 200)];
        var open = CloudPath.ForPolyline(run);
        var closed = CloudPath.ForPolygon(run);
        Assert.False(open.IsEmpty());
        // The interior of the triangle the run would enclose is painted by the polygon and not
        // by the polyline.
        Assert.True(closed.FillContains(new Point(150, 60)));
        Assert.False(open.FillContains(new Point(150, 60)));
    }

    /// <summary>
    /// A cloud polygon's crowns stand OUTSIDE the ring, unclipped. This is the defect the
    /// rectangle cloud had to be inset to avoid: a sized Shape layout-clips its crowns flat.
    /// A poly shape's Path carries no Width/Height, so nothing clips it.
    /// </summary>
    [Fact]
    public void APolygonCloudsCrownsStandOutsideTheRing()
    {
        Point[] square = [new(0, 0), new(200, 0), new(200, 200), new(0, 200)];
        Rect bounds = CloudPath.ForPolygon(square).Bounds;
        Assert.True(bounds.Left < 0, $"crowns should overhang the ring, got {bounds}");
        Assert.True(bounds.Right > 200, $"crowns should overhang the ring, got {bounds}");
    }

    // ------------------------------------------------------------------ what gets committed

    [Fact]
    public void AClosedRunBuildsAFilledPolygonAndAnOpenRunDoesNot() => Sta(() =>
    {
        Point[] run = [new(0, 0), new(40, 0), new(40, 40)];
        var closed = AnnotationFactory.CreatePolyShape(
            run, closed: true, Colors.Red, Color.FromArgb(0x80, 0, 0, 0xFF), 2, LineBorderStyle.Solid);
        var open = AnnotationFactory.CreatePolyShape(
            run, closed: false, Colors.Red, Color.FromArgb(0x80, 0, 0, 0xFF), 2, LineBorderStyle.Solid);

        Assert.Equal(Color.FromArgb(0x80, 0, 0, 0xFF), ((SolidColorBrush)closed.Fill).Color);
        // An open run is never filled: a fill on an unclosed path paints a phantom chord. And
        // the brush is NULL rather than Transparent, because WPF hit-tests a Transparent fill
        // — a transparent polyline would swallow clicks inside an area it does not paint.
        Assert.Null(open.Fill);
    });

    /// <summary>
    /// A poly shape carries no Width/Height, unlike a rectangle. That is load-bearing, not
    /// incidental: WPF layout-clips a Stretch.None Shape to its slot, which is what chopped
    /// the rectangle cloud's crowns flat, and it is why the cloud works here without an inset.
    /// </summary>
    [Fact]
    public void APolyShapeCarriesNoLayoutSlotToClipAgainst() => Sta(() =>
    {
        var path = AnnotationFactory.CreatePolyShape(
            [new(0, 0), new(40, 0), new(40, 40)], closed: true, Colors.Red, Colors.Transparent,
            2, LineBorderStyle.Cloud);
        Assert.True(double.IsNaN(path.Width));
        Assert.True(double.IsNaN(path.Height));
    });

    /// <summary>
    /// Constructing a WPF Shape touches FrameworkElement's input plumbing, which demands an STA
    /// thread. No Application and no window — just the apartment — so this still runs in a
    /// full suite alongside everything else.
    /// </summary>
    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
