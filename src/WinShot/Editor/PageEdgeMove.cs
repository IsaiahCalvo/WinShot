using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Page-edge rule 3 (docs/PAGE-EDGE-RULES.md, Isaiah 2026-11-02): a move stops at the image
/// edge. Survey's clampNudgeDelta (annotationFamilyRules.js), shared there by the pointer
/// move and the arrow keys.
///
/// The selection's union box is clamped as ONE rigid piece, so members never slide apart at
/// an edge. The clamp runs on every frame of the drag, so the mark stops at the edge under
/// the pointer instead of sailing off and snapping back on release. And a box that already
/// hangs over an edge is never pulled back in: it may stay where it is or move further in,
/// never further out.
///
/// Rule 4 (Isaiah 2026-11-02, via the coordinator): a resize or a turn stops at the edge in
/// the same way. <see cref="WithinAllowance"/> says whether a mark's box is still inside what
/// the gesture may use, and <see cref="LastGood"/> finds how far along the pointer's path
/// the gesture can go before it would push the mark further out.
/// </summary>
internal static class PageEdgeMove
{
    /// <param name="start">The selection's union box before the move, in image pixels.</param>
    /// <param name="wanted">The total move the pointer (or key) asks for.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>The total move to apply.</returns>
    public static Vector ClampDelta(Rect start, Vector wanted, double width, double height)
    {
        if (start.IsEmpty || !(width > 0) || !(height > 0)) return wanted;
        if (!double.IsFinite(wanted.X) || !double.IsFinite(wanted.Y)) return default;
        double minDx = Math.Min(0, -start.Left);
        double maxDx = Math.Max(0, width - start.Right);
        double minDy = Math.Min(0, -start.Top);
        double maxDy = Math.Max(0, height - start.Bottom);
        return new Vector(
            Math.Max(minDx, Math.Min(maxDx, wanted.X)),
            Math.Max(minDy, Math.Min(maxDy, wanted.Y)));
    }

    /// <summary>
    /// Is <paramref name="now"/> inside what a resize or turn may use? Each side may reach the
    /// image edge, or stay as far out as it already was at the start of the gesture
    /// (<paramref name="start"/>), never further. <paramref name="slack"/> absorbs rounding.
    /// </summary>
    public static bool WithinAllowance(Rect start, Rect now, double width, double height, double slack = 0.5)
    {
        if (start.IsEmpty || now.IsEmpty || !(width > 0) || !(height > 0)) return true;
        return now.Left >= Math.Min(0, start.Left) - slack
            && now.Top >= Math.Min(0, start.Top) - slack
            && now.Right <= Math.Max(width, start.Right) + slack
            && now.Bottom <= Math.Max(height, start.Bottom) + slack;
    }

    /// <summary>
    /// The furthest point t in [0, 1] along a path where <paramref name="fits"/> holds, given
    /// that t = 0 fits and t = 1 does not. A bisection, so the mark stops within
    /// 1 / 2^<paramref name="steps"/> of the path from the edge.
    /// </summary>
    public static double LastGood(Func<double, bool> fits, int steps = 10)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < steps; i++)
        {
            double mid = (lo + hi) / 2;
            if (fits(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }
}
