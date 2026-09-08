using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// The pointy nubbin on a pill label, and where it joins the pill. Pure geometry, in the
/// label's own coordinates (0,0 = its top-left), so the rules can be read and tested
/// without a window.
///
/// The tail is a triangle from a short base on the pill's edge out to a tip you drag. The
/// base is found by walking from the pill's centre toward the tip until it leaves the
/// rectangle — which is what keeps the tail attached to the nearest edge as it swings
/// around, instead of to a corner it was assigned once.
/// </summary>
internal static class TextTail
{
    /// <summary>How wide the tail is where it meets the pill, as a fraction of the pill's short side.</summary>
    public const double BaseFraction = 0.42;

    /// <summary>Narrowest and widest the base may get regardless of the pill's size.</summary>
    public const double MinBaseWidth = 6;
    public const double MaxBaseWidth = 22;

    /// <summary>
    /// Where a new pill's tail starts: below and left of the label, far enough out to read
    /// as a pointer rather than a bump, and close enough to still be on the picture.
    /// </summary>
    public static Point DefaultTip(Size pill) => new(
        pill.Width * 0.25,
        pill.Height + Math.Max(14, pill.Height * 0.9));

    public static double BaseWidth(Size pill)
    {
        double shortSide = Math.Min(Math.Max(1, pill.Width), Math.Max(1, pill.Height));
        return Math.Clamp(shortSide * BaseFraction, MinBaseWidth, MaxBaseWidth);
    }

    /// <summary>
    /// Whether the tip is far enough outside the pill to be worth drawing. A tail whose tip
    /// is inside its own pill is a stub sticking out of nothing, so it is not drawn at all.
    /// </summary>
    public static bool IsVisible(Size pill, Point tip)
    {
        var body = new Rect(0, 0, Math.Max(1, pill.Width), Math.Max(1, pill.Height));
        return !body.Contains(tip);
    }

    /// <summary>
    /// The triangle: two base points on the pill's edge, then the tip. Returns null when
    /// the tail should not be drawn at all.
    /// </summary>
    public static Point[]? Triangle(Size pill, Point tip)
    {
        if (!IsVisible(pill, tip)) return null;

        double w = Math.Max(1, pill.Width), h = Math.Max(1, pill.Height);
        var centre = new Point(w / 2, h / 2);
        Vector out_ = tip - centre;
        if (out_.Length < 1e-6) return null;

        Point join = EdgePoint(w, h, out_);
        // The base runs across the direction of travel, so it stays flush with whichever
        // edge the tail is currently leaving from.
        Vector along = out_;
        along.Normalize();
        var across = new Vector(-along.Y, along.X);
        double half = BaseWidth(new Size(w, h)) / 2;

        return
        [
            join + across * half,
            join - across * half,
            tip,
        ];
    }

    /// <summary>
    /// Where the ray from the pill's centre in direction <paramref name="dir"/> leaves the
    /// pill's rectangle. The rectangle, not the rounded outline: a base a corner-radius
    /// inside the curve is invisible under the pill anyway, and solving against the
    /// straight edges is exact.
    /// </summary>
    private static Point EdgePoint(double w, double h, Vector dir)
    {
        var centre = new Point(w / 2, h / 2);
        // How far along the ray each axis's edge is; the nearer one is the edge it leaves by.
        double tx = Math.Abs(dir.X) < 1e-9 ? double.PositiveInfinity : (w / 2) / Math.Abs(dir.X);
        double ty = Math.Abs(dir.Y) < 1e-9 ? double.PositiveInfinity : (h / 2) / Math.Abs(dir.Y);
        double t = Math.Min(tx, ty);
        return centre + dir * t;
    }
}
