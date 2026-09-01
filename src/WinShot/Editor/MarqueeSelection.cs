using System.Windows;

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
    /// <summary>A drag shorter than this (content px) is a click, not a marquee.</summary>
    public const double MinDragPx = 3;

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
    /// Whether an annotation's bounds are caught by the marquee.
    /// Window needs full containment; Crossing needs only a touch.
    /// </summary>
    public static bool Catches(Rect marquee, Rect bounds, MarqueeMode mode)
    {
        if (bounds.IsEmpty) return false;
        return mode == MarqueeMode.Window
            ? marquee.Contains(bounds)
            : marquee.IntersectsWith(bounds);
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
