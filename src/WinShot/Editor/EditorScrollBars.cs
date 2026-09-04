using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// The two thin overlay bars that say where you are in a zoomed-in image. The editor pans
/// an infinite canvas rather than hosting a ScrollViewer, so at 400 % on a tall capture
/// there was nothing on screen that said "there is more above this". These are indicators,
/// not controls — they are never hit-tested.
/// </summary>
internal static class EditorScrollBars
{
    /// <summary>Thickness of a bar, and its inset from the viewport edge (DIP).</summary>
    public const double Thickness = 4;
    public const double Margin = 3;

    /// <summary>
    /// Where the bars go, in viewport coordinates. A null component means that axis fits
    /// entirely on screen and needs no bar at all.
    ///
    /// <paramref name="translate"/> is the same offset the view transform carries: the
    /// content's top-left in viewport coordinates.
    /// </summary>
    public static (Rect? Horizontal, Rect? Vertical) Calculate(
        Size viewport, Size content, double zoom, Vector translate)
    {
        if (viewport.Width < 1 || viewport.Height < 1 ||
            content.Width < 1 || content.Height < 1 ||
            !double.IsFinite(zoom) || zoom <= 0)
            return (null, null);

        double scaledW = content.Width * zoom;
        double scaledH = content.Height * zoom;

        Rect? horizontal = Bar(viewport.Width, scaledW, translate.X, horizontal: true, viewport);
        Rect? vertical = Bar(viewport.Height, scaledH, translate.Y, horizontal: false, viewport);
        return (horizontal, vertical);
    }

    private static Rect? Bar(double viewportExtent, double contentExtent, double offset, bool horizontal, Size viewport)
    {
        // A gap of a pixel or two is rounding, not scrollable content.
        if (contentExtent <= viewportExtent + 1) return null;

        double track = Math.Max(0, (horizontal ? viewport.Width : viewport.Height) - Margin * 2);
        double length = Math.Max(Thickness * 3, track * (viewportExtent / contentExtent));

        // How far through the scrollable range the view is, 0..1. offset is the content's
        // top-left, so it runs from 0 (pinned to the start) down to viewport-content.
        double range = contentExtent - viewportExtent;
        double progress = Math.Clamp(-offset / range, 0, 1);
        double start = Margin + (track - length) * progress;

        return horizontal
            ? new Rect(start, viewport.Height - Margin - Thickness, length, Thickness)
            : new Rect(viewport.Width - Margin - Thickness, start, Thickness, length);
    }
}
