using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Which marquee a drag produced. The AutoCAD convention Survey uses: the direction of
/// the drag picks the rule, so there is no modifier to remember and no mode to set.
/// </summary>
internal enum MarqueeMode
{
    /// <summary>Dragged left → right. Selects only annotations wholly inside the box.</summary>
    Window,

    /// <summary>Dragged right → left. Selects anything the box touches.</summary>
    Crossing,
}

/// <summary>
/// Rubber-band selection maths. Ported from Survey's Phase 19 marquee
/// (SVGAnnotationLayer's marqueeRect / marqueeDirection).
/// </summary>
internal static class MarqueeSelection
{
    /// <summary>A drag shorter than this (content px) is a click, not a marquee. Survey MIN_DRAG_PX.</summary>
    public const double MinDragPx = 5;

    /// <summary>Window when the drag ran left→right, Crossing when it ran right→left.</summary>
    public static MarqueeMode ModeFor(Point start, Point current) =>
        current.X >= start.X ? MarqueeMode.Window : MarqueeMode.Crossing;

    /// <summary>The normalised rectangle spanned by a drag.</summary>
    public static Rect RectFor(Point start, Point current) => new(
        Math.Min(start.X, current.X),
        Math.Min(start.Y, current.Y),
        Math.Abs(current.X - start.X),
        Math.Abs(current.Y - start.Y));

    /// <summary>True once the drag is long enough to mean a marquee rather than a click.</summary>
    public static bool IsDrag(Point start, Point current) =>
        Math.Abs(current.X - start.X) >= MinDragPx || Math.Abs(current.Y - start.Y) >= MinDragPx;

    /// <summary>
    /// Whether an annotation is caught by the marquee (Survey marqueeSelection.js:215-235).
    /// Window is a plain AABB containment of the mark's true geometry bounds. Crossing does
    /// the same cheap bbox reject (isBBoxOverlapping) and then a REAL geometry probe, so a
    /// band clipping only the empty corner of a diagonal line's box misses it — the classic
    /// AutoCAD false positive.
    /// </summary>
    /// <param name="geometry">The mark's outline in canvas space, or null to fall back to bounds.</param>
    public static bool Catches(Rect marquee, Rect bounds, MarqueeMode mode, Geometry? geometry = null)
    {
        if (bounds.IsEmpty) return false;
        if (mode == MarqueeMode.Window) return marquee.Contains(bounds);
        if (!marquee.IntersectsWith(bounds)) return false;
        if (geometry is null) return true;
        var band = new RectangleGeometry(marquee);
        return geometry.FillContainsWithDetail(band) != IntersectionDetail.Empty;
    }

    /// <summary>The union of every selected annotation's bounds — the group frame.</summary>
    public static Rect Union(IEnumerable<Rect> bounds)
    {
        Rect result = Rect.Empty;
        foreach (Rect b in bounds)
        {
            if (b.IsEmpty) continue;
            result = result.IsEmpty ? b : Rect.Union(result, b);
        }
        return result;
    }
}
