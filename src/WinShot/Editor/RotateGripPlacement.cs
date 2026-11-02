using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Page-edge rule 1b (docs/PAGE-EDGE-RULES.md, Isaiah 2026-11-02): the rotate button can
/// always be reached.
///
/// It floats above the selection frame, and the selection chrome draws over the image edge
/// onto the desk (rule 1), so above most marks there is room. The one place there is not is
/// where the reachable view itself ends above the button: the view is clamped
/// (<c>ClampView</c>), so above a mark at the top of the image there is only the desk the
/// clamp leaves - less the settings row that lies over the top of the canvas. There, and only
/// there, the button moves to the other side of the frame (below it).
///
/// The test is made against everything panning can ever show, not the part on screen now,
/// so panning never moves the button; only the image's own edge does.
/// </summary>
internal static class RotateGripPlacement
{
    /// <summary>
    /// Every content point (image pixels) the clamped view can ever bring into sight, given
    /// the viewport size, the image size, the zoom, ClampView's margin and how much of the
    /// viewport's top is covered by the settings row.
    /// </summary>
    public static Rect ReachableContent(
        double viewWidth, double viewHeight,
        double contentWidth, double contentHeight,
        double zoom, double margin, double topInset = 0)
    {
        if (!(zoom > 0) || !double.IsFinite(zoom)) return Rect.Empty;
        var (left, right) = Axis(viewWidth, contentWidth, zoom, margin, 0);
        var (top, bottom) = Axis(viewHeight, contentHeight, zoom, margin, Math.Max(0, topInset));
        if (!(right > left) || !(bottom > top)) return Rect.Empty;
        return new Rect(left, top, right - left, bottom - top);
    }

    // One axis of ReachableContent, mirroring EditorWindow.ClampViewAxis: content that fits
    // is centred and never moves; content that does not pans between a margin at either end.
    private static (double Start, double End) Axis(double view, double content, double zoom, double margin, double startInset)
    {
        double size = content * zoom;
        if (size + 2 * margin <= view)
        {
            double offset = (view - size) / 2;
            return ((startInset - offset) / zoom, (view - offset) / zoom);
        }
        return ((startInset - margin) / zoom, content + margin / zoom);
    }

    /// <summary>
    /// Should the rotate button sit below the frame? <paramref name="above"/> and
    /// <paramref name="below"/> are the button's centre on each side, already turned with the
    /// mark; <paramref name="radius"/> is its radius, all in content units. True only when the
    /// usual spot does not fit in <paramref name="reachable"/> and the far spot does; when
    /// neither fits (a mark bigger than the window) the button stays where it always is.
    /// </summary>
    public static bool ShouldPlaceBelow(Point above, Point below, double radius, Rect reachable)
    {
        if (reachable.IsEmpty) return false;
        if (Fits(above, radius, reachable)) return false;
        return Fits(below, radius, reachable);
    }

    private static bool Fits(Point centre, double radius, Rect area)
    {
        double r = Math.Max(0, radius);
        const double slack = 0.01;
        return centre.X - r >= area.Left - slack && centre.X + r <= area.Right + slack
            && centre.Y - r >= area.Top - slack && centre.Y + r <= area.Bottom + slack;
    }
}
