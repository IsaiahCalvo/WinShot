using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Survey Shottr-style counter pin: a circle with a nub at
/// <paramref name="pointerAngleDegrees"/> (0 = east, 225 = southwest default).
/// </summary>
internal static class CounterGeometry
{
    public const double DefaultPointerAngle = 225;

    /// <summary>
    /// Survey: <c>COUNTER_RADIUS = Math.max(4, Number(strokeWidth) || 14)</c>
    /// (PDFViewer.jsx:31610) — the radius IS the toolbar Size value, floored at 4.
    /// </summary>
    public static double RadiusFor(double thickness) => Math.Max(4, thickness);

    public static double TipDistance(double radius) => radius * 1.5;

    public static PathGeometry Pin(Point body, double radius, double pointerAngleDegrees = DefaultPointerAngle)
    {
        double angle = pointerAngleDegrees * Math.PI / 180;
        double tipDist = TipDistance(radius);
        var tip = new Point(body.X + Math.Cos(angle) * tipDist, body.Y + Math.Sin(angle) * tipDist);
        double tangentHalf = Math.Acos(Math.Clamp(radius / tipDist, -1, 1));
        var t1 = new Point(
            body.X + Math.Cos(angle + tangentHalf) * radius,
            body.Y + Math.Sin(angle + tangentHalf) * radius);
        var t2 = new Point(
            body.X + Math.Cos(angle - tangentHalf) * radius,
            body.Y + Math.Sin(angle - tangentHalf) * radius);

        var fig = new PathFigure { StartPoint = tip, IsFilled = true, IsClosed = true };
        fig.Segments.Add(new LineSegment(t1, true));
        fig.Segments.Add(new ArcSegment(
            t2, new Size(radius, radius), 0, isLargeArc: true, SweepDirection.Clockwise, true));
        return new PathGeometry { Figures = { fig }, FillRule = FillRule.Nonzero };
    }

    /// <summary>
    /// Survey's getCounterLabelLayout (counterGeometry.js:10-33): <c>Math.min(baseFontSize,
    /// widthLimitedFontSize)</c> with NO lower bound — the comment at :13-14 records deleting the
    /// old absolute floor, which "outgrew counters below radius 6". Do not reintroduce one.
    /// </summary>
    public static double FontSize(double radius, string caption)
    {
        int glyphs = Math.Max(1, caption.Length);
        double baseSize = radius * 1.05;
        double widthLimited = (radius * 1.55) / (glyphs * 0.7);
        return Math.Min(baseSize, widthLimited);
    }
}
