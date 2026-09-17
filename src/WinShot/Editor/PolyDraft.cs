using System.Windows;

namespace WinShot.Editor;

/// <summary>What a finish request should produce.</summary>
/// <param name="Ok">False when the draft is too short — the caller keeps it alive.</param>
/// <param name="FinalType">The annotation type to commit: polygon or polyline.</param>
/// <param name="Points">The compacted vertices.</param>
/// <param name="Closed">True when the run is a closed polygon.</param>
internal readonly record struct PolyFinish(
    bool Ok, string FinalType, IReadOnlyList<Point> Points, bool Closed);

/// <summary>
/// Survey's <c>src/utils/polyDraft.js</c>, ported whole — the state machine behind the Polygon
/// and Polyline tools.
///
/// Both tools are CLICK-TO-PLACE, not drag-out like rectangle and ellipse: each click commits
/// one vertex, a rubber band follows the cursor, and the shape is finished EXPLICITLY — by the
/// checkmark on the first or latest vertex, by Enter, or by clicking back on the first point.
/// Nothing accidental ends the shape, which is the whole reason finishing is never implicit.
///
/// Pure and immutable: every operation returns a new draft rather than mutating, so the editor
/// can hold a draft in a field and a test can drive the rules with no window at all.
/// </summary>
internal sealed record PolyDraft(
    string Tool, IReadOnlyList<Point> Points, Point Preview, bool SnapToFirst)
{
    /// <summary>
    /// Minimum vertices before a draft may be finished AS THAT TYPE: a polygon needs 3 (two
    /// points can only ever be a line), an open polyline needs 2.
    /// </summary>
    public const int PolygonMinPoints = 3;
    public const int PolylineMinPoints = 2;

    /// <summary>Closing ANY draft into a polygon needs a real area — three corners.</summary>
    public const int CloseMinPoints = 3;

    /// <summary>
    /// How close (in SCREEN pixels) the cursor must come to the first vertex before it becomes
    /// a magnetic "click here to close" target. Screen-constant on purpose: geometry scales with
    /// the zoom but hit targets do not, so closing a shape feels identical at 50% and 400%.
    /// </summary>
    public const double FirstPointSnapScreenRadius = 14;

    /// <summary>Radius (SCREEN px) of the invisible hit disc behind each finish checkmark —
    /// larger than the drawn ring so the control is comfortable without looking heavy.</summary>
    public const double FinishControlScreenHitRadius = 20;

    /// <summary>Drawn radius (SCREEN px) of the checkmark ring itself.</summary>
    public const double FinishControlScreenRadius = 9.5;

    public static int MinPoints(string tool) =>
        tool == AnnotationData.TypePolygon ? PolygonMinPoints : PolylineMinPoints;

    public static bool IsPolyTool(string tool) =>
        tool is AnnotationData.TypePolygon or AnnotationData.TypePolyline;

    public static bool IsPolyTool(EditorTool tool) =>
        tool is EditorTool.Polygon or EditorTool.Polyline;

    public static string TypeFor(EditorTool tool) =>
        tool == EditorTool.Polygon ? AnnotationData.TypePolygon : AnnotationData.TypePolyline;

    /// <summary>Start a draft at the first clicked point.</summary>
    public static PolyDraft Create(string tool, Point point) =>
        new(tool, [point], point, SnapToFirst: false);

    /// <summary>
    /// Snap the segment from <paramref name="anchor"/> onto the nearest 45-degree ray. Survey's
    /// Shift behaviour for polygon and polyline segments.
    /// </summary>
    public static Point SnapSegmentAngle(Point anchor, Point point)
    {
        double dx = point.X - anchor.X, dy = point.Y - anchor.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length == 0) return anchor;
        const double step = Math.PI / 4;
        double angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
        return new Point(anchor.X + Math.Cos(angle) * length, anchor.Y + Math.Sin(angle) * length);
    }

    /// <summary>Append a vertex. Shift snaps the new segment to 45 degrees.</summary>
    public PolyDraft AddPoint(Point point, bool shift = false)
    {
        Point next = shift && Points.Count > 0 ? SnapSegmentAngle(Points[^1], point) : point;
        return this with { Points = [.. Points, next], Preview = next, SnapToFirst = false };
    }

    /// <summary>
    /// Move the rubber-band end. Inside the first vertex's magnetic radius (and only when
    /// closing is legal) the preview snaps ONTO that vertex, so the user SEES the shape close
    /// before committing to the click.
    /// </summary>
    public PolyDraft UpdatePreview(Point point, bool shift = false, double snapRadius = 0)
    {
        bool snap = IsNearFirstPoint(point, snapRadius);
        Point preview = snap
            ? Points[0]
            : (shift && Points.Count > 0 ? SnapSegmentAngle(Points[^1], point) : point);
        return this with { Preview = preview, SnapToFirst = snap };
    }

    /// <summary>Can this draft be finished as its own type?</summary>
    public bool CanFinish => Points.Count >= MinPoints(Tool);

    /// <summary>Can this draft be closed into a polygon?</summary>
    public bool CanClose => Points.Count >= CloseMinPoints;

    /// <summary>
    /// The two vertices that carry a finish checkmark: the FIRST point (closes the shape) and
    /// the LATEST point (finishes it where it stands). Drawboard-style.
    /// </summary>
    public (Point First, Point Last) FinishControls => (Points[0], Points[^1]);

    /// <summary>
    /// True when <paramref name="point"/> is inside the first vertex's magnetic close radius.
    /// The radius is in CONTENT units — the caller divides the screen constant by the zoom.
    /// </summary>
    public bool IsNearFirstPoint(Point point, double radius)
    {
        if (!CanClose || !(radius > 0)) return false;
        Point first = Points[0];
        double dx = point.X - first.X, dy = point.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= radius;
    }

    /// <summary>
    /// Drop trailing points that landed on top of their predecessor — a double-click, or a
    /// jittery one — so a three-click polygon is not rejected for a degenerate final edge.
    /// </summary>
    public static IReadOnlyList<Point> Compact(IReadOnlyList<Point> points, double epsilon = 3)
    {
        var list = points.ToList();
        while (list.Count > 1)
        {
            Vector d = list[^1] - list[^2];
            if (d.Length >= epsilon) break;
            list.RemoveAt(list.Count - 1);
        }
        return list;
    }

    /// <summary>
    /// Resolve what a finish or close request should produce.
    /// <list type="bullet">
    /// <item>finish on a polygon draft -> closed polygon.</item>
    /// <item>finish on a polyline draft -> open polyline.</item>
    /// <item>close on either -> polygon. This is how a polyline becomes a polygon.</item>
    /// </list>
    /// Returns <c>Ok = false</c> when the draft is too short, so the caller leaves the draft
    /// ALIVE instead of silently discarding the corners the user already placed.
    /// </summary>
    public PolyFinish Resolve(bool close)
    {
        string finalType = close ? AnnotationData.TypePolygon : Tool;
        var points = Compact(Points);
        if (points.Count < MinPoints(finalType))
            return new PolyFinish(false, finalType, points, false);
        return new PolyFinish(true, finalType, points, finalType == AnnotationData.TypePolygon);
    }

    /// <summary>
    /// Replace ONE vertex and leave every other byte-identical — Survey's
    /// <c>movePolyVertexPoints</c>, shared with the live vertex drag so "drag one corner" can
    /// never disturb a neighbour by a sub-pixel rounding wobble.
    /// </summary>
    public static Point[] MoveVertex(IReadOnlyList<Point> points, int index, Point target)
    {
        var list = points.ToArray();
        if (index < 0 || index >= list.Length) return list;
        list[index] = target;
        return list;
    }
}
