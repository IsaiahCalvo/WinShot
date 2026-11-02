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
}
