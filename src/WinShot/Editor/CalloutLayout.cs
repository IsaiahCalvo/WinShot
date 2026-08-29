using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Geometry for a Survey-style callout: arrow tip, knee, text box, and the
/// point where the leader joins the box border.
/// </summary>
internal readonly record struct CalloutLayout(Point Tip, Point Knee, Rect Box, Point Join)
{
    public const double DefaultBoxWidth = 120;
    public const double DefaultBoxHeight = 32;
    public const double MinBoxWidth = 48;
    public const double MinBoxHeight = 28;
    public const double MinDrag = 4;
    public const double MinKneeToTip = 11;
    public const double CreateKneeLength = 40;
    public const double TextPadding = 6;

    public static CalloutLayout FromDrag(Point tip, Point current, Size? boxSize = null)
    {
        double w = boxSize?.Width > 0 ? boxSize.Value.Width : DefaultBoxWidth;
        double h = boxSize?.Height > 0 ? boxSize.Value.Height : DefaultBoxHeight;
        var box = new Rect(current.X, current.Y, w, h);
        return FromParts(tip, DefaultKnee(tip, box), box);
    }

    public static Point DefaultKnee(Point tip, Rect box)
    {
        Point center = new(box.X + box.Width / 2, box.Y + box.Height / 2);
        Vector v = center - tip;
        if (v.Length < 1) v = new Vector(CreateKneeLength, -CreateKneeLength * 0.5);
        v.Normalize();
        double length = Math.Max(MinKneeToTip, CreateKneeLength);
        return tip + v * length;
    }

    public static CalloutLayout FromParts(Point tip, Point knee, Rect box)
    {
        double x = Math.Min(box.X, box.X + box.Width);
        double y = Math.Min(box.Y, box.Y + box.Height);
        double w = Math.Abs(box.Width);
        double h = Math.Abs(box.Height);
        box = new Rect(x, y, Math.Max(MinBoxWidth, w), Math.Max(MinBoxHeight, h));
        Point join = ClosestBorderPoint(knee, box);
        if ((knee - tip).Length < MinKneeToTip)
        {
            var v = join - tip;
            if (v.Length < 0.5) v = new Vector(MinKneeToTip, 0);
            v.Normalize();
            knee = tip + v * MinKneeToTip;
            join = ClosestBorderPoint(knee, box);
        }
        return new CalloutLayout(tip, knee, box, join);
    }

    public static Point ClosestBorderPoint(Point point, Rect box)
    {
        double left = box.Left, top = box.Top, right = box.Right, bottom = box.Bottom;
        double cx = Math.Clamp(point.X, left, right);
        double cy = Math.Clamp(point.Y, top, bottom);

        if (point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom)
        {
            double dL = Math.Abs(point.X - left);
            double dR = Math.Abs(point.X - right);
            double dT = Math.Abs(point.Y - top);
            double dB = Math.Abs(point.Y - bottom);
            double min = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
            if (min == dL) return new Point(left, cy);
            if (min == dR) return new Point(right, cy);
            if (min == dT) return new Point(cx, top);
            return new Point(cx, bottom);
        }

        return new Point(cx, cy);
    }

    public Rect Bounds()
    {
        double x1 = Math.Min(Math.Min(Tip.X, Knee.X), Box.Left);
        double y1 = Math.Min(Math.Min(Tip.Y, Knee.Y), Box.Top);
        double x2 = Math.Max(Math.Max(Tip.X, Knee.X), Box.Right);
        double y2 = Math.Max(Math.Max(Tip.Y, Knee.Y), Box.Bottom);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    public CalloutLayout Translate(double dx, double dy) =>
        FromParts(
            new Point(Tip.X + dx, Tip.Y + dy),
            new Point(Knee.X + dx, Knee.Y + dy),
            new Rect(Box.X + dx, Box.Y + dy, Box.Width, Box.Height));

    public CalloutLayout WithTip(Point tip) => FromParts(tip, Knee, Box);
    public CalloutLayout WithKnee(Point knee) => FromParts(Tip, knee, Box);
    public CalloutLayout WithBox(Rect box) => FromParts(Tip, Knee, box);
}
