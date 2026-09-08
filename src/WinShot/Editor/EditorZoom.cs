using System.Windows;

namespace WinShot.Editor;

/// <summary>Where the canvas sits after a zoom: the scale, and the view's translate in viewport pixels.</summary>
public readonly record struct ZoomView(double Zoom, Vector Translate);

/// <summary>
/// The arithmetic behind the editor's targeted zooms. The editor pans an infinite canvas
/// rather than hosting a ScrollViewer, so "show me this" is always a scale plus a
/// translate — and both are pure functions of the content rectangle and the viewport,
/// which is what lets them be read and tested without a window.
/// </summary>
internal static class EditorZoom
{
    /// <summary>
    /// The view that puts <paramref name="content"/> (in content pixels) centred and as
    /// large as it will go inside <paramref name="viewport"/>, with a little breathing room
    /// so the thing being zoomed to is not flush against the edges.
    /// A degenerate rectangle has no size to fit, so it is treated as a point: the zoom
    /// stays where it was and only the centring moves.
    /// </summary>
    public static ZoomView FitRect(
        Rect content, Size viewport, double currentZoom, double minZoom, double maxZoom, double padding = 24)
    {
        double zoom = currentZoom;
        if (content.Width > 0.5 && content.Height > 0.5 &&
            viewport.Width > 1 && viewport.Height > 1)
        {
            double usableW = Math.Max(1, viewport.Width - 2 * padding);
            double usableH = Math.Max(1, viewport.Height - 2 * padding);
            zoom = Math.Min(usableW / content.Width, usableH / content.Height);
        }
        zoom = Math.Clamp(zoom, minZoom, maxZoom);

        // Rect.Empty's Left is +Inf and its Width is -Inf, so the midpoint of an empty
        // content rect is NaN — which would ride into the view transform and blank the
        // canvas. An unusable centre means "do not move".
        var centre = new Point(
            Finite(content.Left + content.Width / 2),
            Finite(content.Top + content.Height / 2));
        return new ZoomView(zoom, CentreOn(centre, zoom, viewport));
    }

    /// <summary>
    /// The view that puts one content point in the middle of the viewport at
    /// <paramref name="zoom"/>. This is what a corner zoom is: the corner does not go to
    /// the corner of the screen, it goes where you are looking.
    /// </summary>
    public static ZoomView CentreOnPoint(Point contentPoint, Size viewport, double zoom, double minZoom, double maxZoom)
    {
        zoom = Math.Clamp(zoom, minZoom, maxZoom);
        return new ZoomView(zoom, CentreOn(contentPoint, zoom, viewport));
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static Vector CentreOn(Point contentPoint, double zoom, Size viewport) => new(
        viewport.Width / 2 - contentPoint.X * zoom,
        viewport.Height / 2 - contentPoint.Y * zoom);

    /// <summary>
    /// The corner a corner-zoom aims at. Q takes the top-left, W the bottom-right — of the
    /// selection when there is one, and of the whole picture when there is not, so the keys
    /// still mean something with nothing selected.
    /// </summary>
    public static Point Corner(Rect target, bool bottomRight) =>
        bottomRight ? target.BottomRight : target.TopLeft;
}
