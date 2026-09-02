using System.Windows;

namespace WinShot.Editor;

/// <summary>
/// Geometry for a Survey-style callout: arrow tip, knee, text box, and the
/// point where the leader joins the box border.
/// <para>
/// The connection rules are a literal port of Survey's
/// <c>calculateCalloutConnection</c> (src/utils/calloutGeometry.js): the leader
/// normally joins the box edge nearest the knee, but whenever the geometry is
/// bad — knee inside the box, knee sitting on the border, knee stacked with the
/// tip, or the tip→knee segment cutting the box — it re-routes off the border
/// point nearest the ARROW and rebuilds the knee along that border→tip ray.
/// </para>
/// </summary>
internal readonly record struct CalloutLayout(Point Tip, Point Knee, Rect Box, Point Join)
{
    public const double DefaultBoxWidth = 120;
    public const double DefaultBoxHeight = 32;
    // Survey's corner-resize floor (useSVGInteraction.js: minW/minH = 20).
    public const double MinBoxWidth = 20;
    public const double MinBoxHeight = 20;
    public const double MinDrag = 4;
    /// <summary>Survey HANDLE_RADIUS (handleStyle.js:40).</summary>
    public const double HandleRadius = 5.5;
    /// <summary>Survey MIN_KNEE_TO_ARROW_DISTANCE = HANDLE_RADIUS * 2 + HANDLE_CLEAR_GAP(0).</summary>
    public const double MinKneeToTip = HandleRadius * 2;
    /// <summary>Survey MIN_KNEE_TO_BOX_EDGE_DISTANCE = HANDLE_RADIUS + HANDLE_CLEAR_GAP(0).</summary>
    public const double MinKneeToBoxEdge = HandleRadius;
    /// <summary>Survey MIN_SEGMENT_LENGTH (calloutGeometry.js:27).</summary>
    public const double MinSegmentLength = 10;
    /// <summary>Survey STACKED_THRESHOLD (calloutGeometry.js:9).</summary>
    public const double StackedThreshold = 2;
    /// <summary>Survey creation knee radius: 40 px out from the tip toward the box centre.</summary>
    public const double CreateKneeLength = 40;
    /// <summary>Survey creation forbidden-zone clearance around the tip (SVGAnnotationLayer.jsx:1091).</summary>
    public const double TipClearance = 10;
    public const double TextPadding = 6;
    /// <summary>Survey descenderBuffer = fontSize * 0.35 (svgAnnotationRenderers.jsx:1375).</summary>
    public const double DescenderRatio = 0.35;
    /// <summary>Survey defaultCalloutStyle.fontFamily / .fontSize (Callout/types.js:182-183).</summary>
    public const string DefaultFontFamily = "Arial";
    public const double DefaultFontSize = 14;

    /// <summary>
    /// Descender buffer folded into the drawn box height. Stored geometry keeps the
    /// unbuffered <see cref="Box"/>; every visual and hit-testing rule uses
    /// <see cref="RenderBox"/>, exactly as Survey's boxHeightWithDescenders does.
    /// </summary>
    public double Descender { get; init; }

    /// <summary>The box the user actually sees: stored height plus the descender buffer.</summary>
    public Rect RenderBox => Descender > 0
        ? new Rect(Box.X, Box.Y, Box.Width, Box.Height + Descender)
        : Box;

    public static CalloutLayout FromDrag(Point tip, Point current, Size? boxSize = null, double descender = 0)
    {
        double w = boxSize?.Width > 0 ? boxSize.Value.Width : DefaultBoxWidth;
        double h = boxSize?.Height > 0 ? boxSize.Value.Height : DefaultBoxHeight;
        var box = new Rect(current.X, current.Y, w, h);
        return FromParts(tip, DefaultKnee(tip, box), box, descender);
    }

    /// <summary>
    /// Survey creation knee (SVGAnnotationLayer.jsx:1122-1134 and the identical preview at
    /// :4869-4886): 40 px out from the arrow tip along tip → text-box centre, so the knee
    /// swivels around the tip as the box is dragged and the commit matches the ghost exactly.
    /// </summary>
    public static Point DefaultKnee(Point tip, Rect box)
    {
        var v = new Vector(box.X + box.Width / 2 - tip.X, box.Y + box.Height / 2 - tip.Y);
        if (v.Length < 0.5) v = new Vector(CreateKneeLength, 0);
        v.Normalize();
        return tip + v * CreateKneeLength;
    }

    /// <summary>
    /// Survey creation forbidden zone (SVGAnnotationLayer.jsx:1085-1104): the pointer-driven
    /// box top-left can never cover the arrow tip. A box origin inside the (boxW + 10) x
    /// (boxH + 10) rect around the tip is pushed back out through its nearest edge, so the box
    /// slides along the boundary instead of landing on the tip.
    /// </summary>
    public static Point PushBoxOffTip(Point tip, Point origin, Size? boxSize = null)
    {
        double w = boxSize?.Width > 0 ? boxSize.Value.Width : DefaultBoxWidth;
        double h = boxSize?.Height > 0 ? boxSize.Value.Height : DefaultBoxHeight;
        double x0 = tip.X - w - TipClearance, x1 = tip.X + TipClearance;
        double y0 = tip.Y - h - TipClearance, y1 = tip.Y + TipClearance;
        double x = origin.X, y = origin.Y;
        // Strict inequalities, matching Survey: the box may rest exactly on the boundary.
        if (x <= x0 || x >= x1 || y <= y0 || y >= y1) return origin;
        double dL = x - x0, dR = x1 - x, dU = y - y0, dD = y1 - y;
        double least = Math.Min(Math.Min(dL, dR), Math.Min(dU, dD));
        if (least == dL) x = x0;
        else if (least == dR) x = x1;
        else if (least == dU) y = y0;
        else y = y1;
        return new Point(x, y);
    }

    public static CalloutLayout FromParts(Point tip, Point knee, Rect box, double descender = 0)
    {
        box = NormalizeBox(box);
        var rect = descender > 0
            ? new Rect(box.X, box.Y, box.Width, box.Height + descender)
            : box;
        var (join, effective) = Connect(tip, knee, rect);
        return new CalloutLayout(tip, effective, box, join) { Descender = descender };
    }

    /// <summary>
    /// Survey's rawKnee mode (svgAnnotationRenderers.jsx:1393-1404, gated by skipAutoRoute at
    /// SVGAnnotationLayer.jsx:3363): while a handle is down the geometry follows the cursor
    /// 1:1 — no min-size clamp beyond Survey's 20 px floor, no re-route, no knee relocation.
    /// Discipline happens on release.
    /// </summary>
    public static CalloutLayout Raw(Point tip, Point knee, Rect box, double descender = 0)
    {
        box = NormalizeBox(box);
        var rect = descender > 0
            ? new Rect(box.X, box.Y, box.Width, box.Height + descender)
            : box;
        return new CalloutLayout(tip, knee, box, ClampToBox(knee, rect)) { Descender = descender };
    }

    private static Rect NormalizeBox(Rect box)
    {
        double x = Math.Min(box.X, box.X + box.Width);
        double y = Math.Min(box.Y, box.Y + box.Height);
        return new Rect(x, y,
            Math.Max(MinBoxWidth, Math.Abs(box.Width)),
            Math.Max(MinBoxHeight, Math.Abs(box.Height)));
    }

    // ------------------------------------------------- Survey connection rules

    private static (Point Join, Point Knee) Connect(Point tip, Point knee, Rect box)
    {
        Point join, effective;
        // Bad geometry (calloutGeometry.js:263-269) or the tip→knee segment cutting the box
        // (:356-382) both re-route off the border point nearest the ARROW; using the
        // knee-nearest edge is exactly what lets line1 start on the far side and cut back
        // through the box (:457-465).
        if (IsInside(knee, box) || IsOnBorder(knee, box) ||
            (knee - tip).Length <= StackedThreshold ||
            SegmentCrossesRect(tip, knee, box))
            (join, effective) = RouteFromTip(tip, box);
        else
            (join, effective) = (ClampToBox(knee, box), knee);

        // Final pass (calloutGeometry.js:547-560): re-route ONLY when the knee is genuinely
        // inside. Relocating on mere proximity is the jumping-knee bug Survey removed.
        if (IsInside(effective, box))
        {
            effective = PushKneeOutside(tip, effective, box);
            join = (tip - ClosestBorderPoint(tip, box)).Length > 0.001
                ? ClosestBorderPoint(tip, box)
                : ClosestBorderPoint(effective, box);
        }
        return (join, effective);
    }

    /// <summary>Border point nearest the arrow plus a knee rebuilt along border → tip.</summary>
    private static (Point Join, Point Knee) RouteFromTip(Point tip, Rect box)
    {
        Point border = ClosestBorderPoint(tip, box);
        Vector d = tip - border;
        double distance = d.Length;
        double minLine1 = Math.Max(MinKneeToBoxEdge, MinSegmentLength);
        Vector unit = distance > 0.001 ? d / distance : new Vector(0, 0);

        if (distance < minLine1 + MinKneeToTip)
        {
            double distFromBorder = Math.Max(minLine1, distance - MinKneeToTip);
            Point mid = ConstrainKnee(border + unit * distFromBorder, tip, box, border);
            double segment1 = (mid - border).Length;
            double segment2 = (tip - mid).Length;
            if (segment1 < MinSegmentLength || segment2 < MinKneeToTip)
            {
                // Tightest squeeze: park at least one handle radius outside the border.
                double clamped = Math.Max(MinKneeToBoxEdge, Math.Max(0, distance - MinKneeToTip));
                return (border, border + unit * clamped);
            }
            return (border, mid);
        }

        double half = Math.Max(minLine1, Math.Min(distance / 2, distance - MinKneeToTip));
        return (border, ConstrainKnee(border + unit * half, tip, box, border));
    }

    /// <summary>Port of Survey's constrainKneePosition (calloutGeometry.js:118-187).</summary>
    private static Point ConstrainKnee(Point proposed, Point tip, Rect box, Point border)
    {
        Vector d = tip - border;
        double distBorderToArrow = d.Length;
        double minLine1 = Math.Max(MinKneeToBoxEdge, MinSegmentLength);
        Vector unit = distBorderToArrow > 0.001 ? d / distBorderToArrow : new Vector(0, 0);

        if (distBorderToArrow < minLine1 + MinKneeToTip)
        {
            double maxFromBorder = Math.Max(0, distBorderToArrow - MinKneeToTip);
            return border + unit * Math.Min(minLine1, maxFromBorder);
        }

        double projection = (proposed - border) * unit;
        double clamped = Math.Max(minLine1, Math.Min(projection, distBorderToArrow - MinKneeToTip));
        Point knee = border + unit * clamped;

        double toArrow = (tip - knee).Length;
        double toEdge = DistanceToBoxEdge(knee, box);
        if (toArrow < MinKneeToTip && distBorderToArrow > MinKneeToTip)
            knee = border + unit * Math.Max(minLine1, distBorderToArrow - MinKneeToTip);
        if ((toEdge < MinKneeToBoxEdge || toEdge < MinSegmentLength) && distBorderToArrow > minLine1)
            knee = border + unit * Math.Max(minLine1, Math.Min(minLine1, distBorderToArrow - MinKneeToTip));
        return knee;
    }

    public static Point ClosestBorderPoint(Point point, Rect box)
    {
        double left = box.Left, top = box.Top, right = box.Right, bottom = box.Bottom;
        double cx = Math.Clamp(point.X, left, right);
        double cy = Math.Clamp(point.Y, top, bottom);

        if (IsInside(point, box))
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

    /// <summary>
    /// Survey's final inside-only re-route (calloutGeometry.js:556-620). An outside knee is
    /// never relocated — the 2026-05-18 fix that removed the proximity clamp.
    /// </summary>
    public static Point PushKneeOutside(Point tip, Point knee, Rect box)
    {
        if (!IsInside(knee, box)) return knee;

        Point border = ClosestBorderPoint(tip, box);
        Vector d = tip - border;
        double distance = d.Length;
        double minLine1 = Math.Max(MinKneeToBoxEdge, MinSegmentLength);

        if (distance > minLine1 + MinKneeToTip)
            return border + (d / distance) * minLine1;
        if (distance > 0.001)
        {
            double maxFromBorder = Math.Max(0, distance - MinKneeToTip);
            return border + (d / distance) * Math.Max(0, Math.Min(minLine1, maxFromBorder));
        }

        // Arrow sits on the border: push the knee out through its own nearest edge.
        Point edge = ClosestBorderPoint(knee, box);
        Vector away = knee - edge;
        if (away.Length <= 0.001) return new Point(edge.X + minLine1, edge.Y);
        away.Normalize();
        return edge + away * minLine1;
    }

    public static double DistanceToBoxEdge(Point point, Rect box)
    {
        if (IsInside(point, box))
        {
            return Math.Min(
                Math.Min(point.X - box.Left, box.Right - point.X),
                Math.Min(point.Y - box.Top, box.Bottom - point.Y));
        }
        double dx = Math.Max(0, Math.Max(box.Left - point.X, point.X - box.Right));
        double dy = Math.Max(0, Math.Max(box.Top - point.Y, point.Y - box.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static bool IsInside(Point p, Rect box) =>
        p.X >= box.Left && p.X <= box.Right && p.Y >= box.Top && p.Y <= box.Bottom;

    /// <summary>Survey isPointOnBorder (calloutGeometry.js:48-65), STACKED_THRESHOLD = 2.</summary>
    public static bool IsOnBorder(Point p, Rect box, double threshold = StackedThreshold)
    {
        bool onLeft = Math.Abs(p.X - box.Left) <= threshold;
        bool onRight = Math.Abs(p.X - box.Right) <= threshold;
        bool onTop = Math.Abs(p.Y - box.Top) <= threshold;
        bool onBottom = Math.Abs(p.Y - box.Bottom) <= threshold;
        if ((onTop || onBottom) && p.X >= box.Left - threshold && p.X <= box.Right + threshold)
            return true;
        return (onLeft || onRight) && p.Y >= box.Top - threshold && p.Y <= box.Bottom + threshold;
    }

    private static Point ClampToBox(Point p, Rect box) => new(
        Math.Max(box.Left, Math.Min(p.X, box.Right)),
        Math.Max(box.Top, Math.Min(p.Y, box.Bottom)));

    /// <summary>Liang-Barsky clip of a segment against a rect (calloutGeometry.js:356-382).</summary>
    public static bool SegmentCrossesRect(Point a, Point b, Rect box)
    {
        double t0 = 0, t1 = 1;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double[] p = { -dx, dx, -dy, dy };
        double[] q = { a.X - box.Left, box.Right - a.X, a.Y - box.Top, box.Bottom - a.Y };

        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;
                continue;
            }
            double r = q[i] / p[i];
            if (p[i] < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
        }
        return t0 <= t1;
    }

    /// <summary>
    /// Survey's release-time drop rule (useSVGInteraction.js:3529-3585). A drag that ends here
    /// is rolled back whole — tip, knee, box position AND box size — rather than being clamped
    /// mid-flight. Distances are derived from the on-screen handle radius, like Survey's
    /// effHandleR, so they track the visible thumb.
    /// </summary>
    public static bool IsDropSafe(CalloutLayout layout, double handleRadius = HandleRadius)
    {
        var rect = layout.RenderBox;
        double minToBox = handleRadius;
        double minToHandle = handleRadius * 2;
        if (IsInside(layout.Knee, rect) || IsInside(layout.Tip, rect)) return false;
        if (DistanceToBoxEdge(layout.Knee, rect) < minToBox) return false;
        if (DistanceToBoxEdge(layout.Tip, rect) < minToBox) return false;
        if ((layout.Knee - layout.Tip).Length < minToHandle) return false;
        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
        {
            if ((layout.Knee - corner).Length < minToHandle) return false;
            if ((layout.Tip - corner).Length < minToHandle) return false;
        }
        return !SegmentCrossesRect(layout.Knee, layout.Tip, rect);
    }

    public Rect Bounds()
    {
        var rect = RenderBox;
        double x1 = Math.Min(Math.Min(Tip.X, Knee.X), rect.Left);
        double y1 = Math.Min(Math.Min(Tip.Y, Knee.Y), rect.Top);
        double x2 = Math.Max(Math.Max(Tip.X, Knee.X), rect.Right);
        double y2 = Math.Max(Math.Max(Tip.Y, Knee.Y), rect.Bottom);
        return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    public CalloutLayout Translate(double dx, double dy) =>
        FromParts(
            new Point(Tip.X + dx, Tip.Y + dy),
            new Point(Knee.X + dx, Knee.Y + dy),
            new Rect(Box.X + dx, Box.Y + dy, Box.Width, Box.Height),
            Descender);

    public CalloutLayout WithTip(Point tip) => FromParts(tip, Knee, Box, Descender);
    public CalloutLayout WithKnee(Point knee) => FromParts(Tip, knee, Box, Descender);
    public CalloutLayout WithBox(Rect box) => FromParts(Tip, Knee, box, Descender);
    public CalloutLayout WithDescender(double descender) => FromParts(Tip, Knee, Box, descender);
}
