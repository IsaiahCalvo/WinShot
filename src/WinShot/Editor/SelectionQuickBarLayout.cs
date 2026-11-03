using System.Windows;

namespace WinShot.Editor;

/// <summary>What the selection quick bar shows for the current pick.</summary>
internal readonly record struct QuickBarPlan(bool Colour, bool Width, bool Style, bool Duplicate, bool Delete, bool More);

/// <summary>Which side of the selection the bar landed on.</summary>
internal enum QuickBarSide { Above, Below, Pinned }

/// <summary>
/// Survey's selection quick bar (SelectionQuickBar.jsx, utils/selectionQuickBar.js), the pure part.
///
/// A small bar sits just above the selection, centred on it, and drops below only when there is
/// no room above inside the viewer (or when the rotate grip is on top, so the bar never covers
/// it). It never leaves the viewer (10 px margin) and hides when the selection is scrolled away.
/// It holds what people change most: colour, line width, line style, Duplicate, Delete and More
/// (the right-click menu). With several marks picked it shows only what applies to every one.
/// </summary>
internal static class SelectionQuickBarLayout
{
    /// <summary>Space kept between the bar and the edges of the viewer (Drawboard: 10 px).</summary>
    public const double Margin = 10;

    /// <summary>Gap between the bar and the selection frame (textSelectionQuickMenu.js QUICK_MENU_GAP).</summary>
    public const double Gap = 8;

    // Marks with no paint the bar could change. Redaction, spotlight and magnifier are
    // WinShot-only effects with their own strength/style controls, not a colour.
    private static readonly HashSet<string> NoPaint = new(StringComparer.Ordinal)
    {
        AnnotationData.TypeImage, AnnotationData.TypeEmoji, AnnotationData.TypeRedact,
        AnnotationData.TypeSpotlight, AnnotationData.TypeMagnifier,
    };

    // Survey WIDTH_KINDS, in WinShot's type names.
    private static readonly HashSet<string> WidthKinds = new(StringComparer.Ordinal)
    {
        AnnotationData.TypeRectangle, AnnotationData.TypeEllipse, AnnotationData.TypeLine,
        AnnotationData.TypeArrow, AnnotationData.TypeCurvedArrow, AnnotationData.TypePolyline,
        AnnotationData.TypePolygon, AnnotationData.TypeFreehand, AnnotationData.TypeHighlighter,
        AnnotationData.TypeText, AnnotationData.TypeCallout,
    };

    // Survey STYLE_KINDS: solid / dashed / dotted / cloud.
    private static readonly HashSet<string> StyleKinds = new(StringComparer.Ordinal)
    {
        AnnotationData.TypeRectangle, AnnotationData.TypeEllipse, AnnotationData.TypeLine,
        AnnotationData.TypeArrow, AnnotationData.TypePolyline, AnnotationData.TypePolygon,
        AnnotationData.TypeText, AnnotationData.TypeCallout,
    };

    // Lines and arrows are never clouded (pinnedTools.js CLOUD_LINE_TOOLS + rect).
    private static readonly HashSet<string> CloudKinds = new(StringComparer.Ordinal)
    {
        AnnotationData.TypeRectangle, AnnotationData.TypeEllipse, AnnotationData.TypePolygon,
        AnnotationData.TypeText, AnnotationData.TypeCallout,
    };

    /// <summary>
    /// What the bar holds for the picked marks' types. Null when nothing is picked. A locked
    /// mark can't be restyled or deleted: Duplicate and More only.
    /// </summary>
    public static QuickBarPlan? Plan(IReadOnlyCollection<string?> types, bool anyLocked = false)
    {
        if (types.Count == 0) return null;
        bool every(HashSet<string> set) => types.All(t => t is not null && set.Contains(t));
        bool anyNoPaint = types.Any(t => t is null || NoPaint.Contains(t));
        return new QuickBarPlan(
            Colour: !anyLocked && !anyNoPaint,
            Width: !anyLocked && !anyNoPaint && every(WidthKinds),
            Style: !anyLocked && !anyNoPaint && every(StyleKinds),
            Duplicate: true,
            Delete: !anyLocked,
            More: true);
    }

    /// <summary>Whether every picked type can take the cloud line style.</summary>
    public static bool AllowsCloud(IReadOnlyCollection<string?> types) =>
        types.Count > 0 && types.All(t => t is not null && CloudKinds.Contains(t));

    /// <summary>
    /// Where the bar goes (computeSelectionQuickBarPosition): centred on the on-screen part of
    /// the selection, just above everything it must keep clear of, or below when
    /// <paramref name="preferBelow"/> or when there is no room above. When neither side fits it
    /// pins to whichever viewer edge covers least — a grip first, then the selection. Null when
    /// the selection is wholly out of view (the bar hides).
    /// </summary>
    public static (double Left, double Top, QuickBarSide Side)? Position(
        Rect selection, IReadOnlyList<Rect> keepOut, bool preferBelow, Rect bounds,
        double barWidth, double barHeight)
    {
        if (selection.IsEmpty || bounds.IsEmpty) return null;
        if (selection.Bottom <= bounds.Top || selection.Top >= bounds.Bottom
            || selection.Right <= bounds.Left || selection.Left >= bounds.Right)
            return null;

        double width = Math.Max(1, Math.Min(barWidth, bounds.Width - Margin * 2));
        double height = Math.Max(1, barHeight);
        double minLeft = bounds.Left + Margin;
        double maxLeft = bounds.Right - Margin - width;
        double minTop = bounds.Top + Margin;
        double maxTop = bounds.Bottom - Margin - height;

        double centre = (Math.Max(selection.Left, bounds.Left) + Math.Min(selection.Right, bounds.Right)) / 2;
        double left = Math.Round(Math.Max(minLeft, Math.Min(maxLeft, centre - width / 2)));

        Rect guards = selection;
        foreach (Rect r in keepOut)
            if (!r.IsEmpty) guards.Union(r);

        double aboveTop = guards.Top - Gap - height;
        double belowTop = guards.Bottom + Gap;
        foreach (QuickBarSide side in preferBelow
                     ? new[] { QuickBarSide.Below, QuickBarSide.Above }
                     : new[] { QuickBarSide.Above, QuickBarSide.Below })
        {
            if (side == QuickBarSide.Above && aboveTop >= minTop) return (left, Math.Round(aboveTop), side);
            if (side == QuickBarSide.Below && belowTop <= maxTop) return (left, Math.Round(belowTop), side);
        }

        double Cost(double top)
        {
            var bar = new Rect(left, top, width, height);
            double grips = keepOut.Sum(r => Overlap(bar, r));
            return grips * 1000 + Overlap(bar, selection);
        }
        double low = Math.Round(Math.Max(minTop, maxTop));
        double high = Math.Round(minTop);
        return (left, Cost(high) < Cost(low) ? high : low, QuickBarSide.Pinned);
    }

    private static double Overlap(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        double w = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double h = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return w * h;
    }
}
