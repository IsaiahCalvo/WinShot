using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Lining a mark up with the marks already on the canvas. Pure geometry: given where a
/// mark would land and where everything else is, it returns the nudge that puts an edge or
/// a centre exactly on a neighbour's.
///
/// Each axis snaps independently — a mark can align its left edge with one neighbour and
/// its middle with another — and the SMALLEST available correction wins on each axis, so
/// the mark goes to the nearest guide rather than the first one considered.
/// </summary>
internal static class ObjectSnap
{
    /// <summary>How close an edge has to get, in content pixels, before it sticks.</summary>
    public const double Tolerance = 6;

    public static Vector Adjust(Rect moving, IEnumerable<Rect> others, double tolerance = Tolerance)
    {
        if (moving.IsEmpty || tolerance <= 0) return default;

        double bestX = double.NaN, bestY = double.NaN;
        double[] movingX = { moving.Left, moving.Left + moving.Width / 2, moving.Right };
        double[] movingY = { moving.Top, moving.Top + moving.Height / 2, moving.Bottom };

        foreach (Rect other in others)
        {
            if (other.IsEmpty) continue;
            double[] otherX = { other.Left, other.Left + other.Width / 2, other.Right };
            double[] otherY = { other.Top, other.Top + other.Height / 2, other.Bottom };

            foreach (double from in movingX)
                foreach (double to in otherX)
                    Consider(from, to, tolerance, ref bestX);

            foreach (double from in movingY)
                foreach (double to in otherY)
                    Consider(from, to, tolerance, ref bestY);
        }

        return new Vector(
            double.IsNaN(bestX) ? 0 : bestX,
            double.IsNaN(bestY) ? 0 : bestY);
    }

    private static void Consider(double from, double to, double tolerance, ref double best)
    {
        double delta = to - from;
        if (Math.Abs(delta) > tolerance) return;
        if (double.IsNaN(best) || Math.Abs(delta) < Math.Abs(best))
            best = delta;
    }
}
