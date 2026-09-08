using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// The eight grab points on an adjustable selection — where they sit, which one the cursor
/// is over, what dragging one does to the rectangle, and how they are drawn. Shared by both
/// selectors: the all-in-one restored its last region as a rectangle you could only accept
/// or redraw, because this lived privately inside the region selector.
///
/// Everything here is pure geometry in one coordinate space; the caller decides whether that
/// space is screen or local pixels and passes the clamp bounds it wants.
/// </summary>
public static class SelectionHandles
{
    /// <summary>Half-extent of a handle dot, so a dot is <c>2 * Half</c> px across.</summary>
    public const int Half = 5;

    /// <summary>Grab tolerance around a handle centre. Wider than the dot on purpose.</summary>
    public const int HitTolerance = 9;

    /// <summary>The eight anchor points: TL, Top, TR, Right, BR, Bottom, BL, Left.</summary>
    public static SD.Point[] Points(SD.Rectangle r)
    {
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        return
        [
            new(r.Left, r.Top), new(cx, r.Top), new(r.Right, r.Top),
            new(r.Right, cy), new(r.Right, r.Bottom),
            new(cx, r.Bottom), new(r.Left, r.Bottom), new(r.Left, cy),
        ];
    }

    /// <summary>Index of the handle under <paramref name="cursor"/>, or -1.</summary>
    public static int HitTest(SD.Point cursor, SD.Rectangle r)
    {
        SD.Point[] points = Points(r);
        for (int i = 0; i < points.Length; i++)
        {
            if (Math.Abs(cursor.X - points[i].X) <= HitTolerance &&
                Math.Abs(cursor.Y - points[i].Y) <= HitTolerance)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The rectangle after dragging handle <paramref name="handle"/> to
    /// <paramref name="cursor"/>, normalized (dragging a corner past its opposite flips the
    /// rectangle rather than inverting it) and clipped to <paramref name="bounds"/>.
    /// </summary>
    public static SD.Rectangle Resize(SD.Rectangle start, int handle, SD.Point cursor, SD.Rectangle bounds)
    {
        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        switch (handle)
        {
            case 0: l = cursor.X; t = cursor.Y; break;            // TL
            case 1: t = cursor.Y; break;                          // Top
            case 2: r = cursor.X; t = cursor.Y; break;            // TR
            case 3: r = cursor.X; break;                          // Right
            case 4: r = cursor.X; b = cursor.Y; break;            // BR
            case 5: b = cursor.Y; break;                          // Bottom
            case 6: l = cursor.X; b = cursor.Y; break;            // BL
            case 7: l = cursor.X; break;                          // Left
            default: return start;
        }
        var rect = SD.Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        rect.Intersect(bounds);
        // Rectangle.Intersect answers Rectangle.Empty — (0,0,0,0) — when the two do not
        // overlap at all, and the min-size clamp below would turn that into an 8x8 selection
        // parked at the desktop origin instead of anywhere near the drag. Keep the rect the
        // drag started from instead.
        if (rect.IsEmpty)
            return start;
        // A handle dragged onto its opposite corner used to leave a 0x0 rect that Enter and
        // Done then silently refused to confirm — a dead end with all eight handles stacked
        // on one point and nothing on screen saying why nothing happened.
        rect.Width = Math.Max(rect.Width, MinSizePx);
        rect.Height = Math.Max(rect.Height, MinSizePx);
        if (rect.Right > bounds.Right) rect.X = bounds.Right - rect.Width;
        if (rect.Bottom > bounds.Bottom) rect.Y = bounds.Bottom - rect.Height;
        return rect;
    }

    /// <summary>Smallest selection a handle drag can leave behind.</summary>
    public const int MinSizePx = 8;

    /// <summary>The rectangle moved by (dx, dy), pushed back inside <paramref name="bounds"/>
    /// rather than clipped — a move must never also resize.</summary>
    public static SD.Rectangle Move(SD.Rectangle start, int dx, int dy, SD.Rectangle bounds)
    {
        var rect = new SD.Rectangle(start.X + dx, start.Y + dy, start.Width, start.Height);
        if (rect.Left < bounds.Left) rect.X = bounds.Left;
        if (rect.Top < bounds.Top) rect.Y = bounds.Top;
        if (rect.Right > bounds.Right) rect.X = bounds.Right - rect.Width;
        if (rect.Bottom > bounds.Bottom) rect.Y = bounds.Bottom - rect.Height;
        return rect;
    }

    /// <summary>The resize cursor for a handle index; null when the index is not a handle.</summary>
    public static WF.Cursor? CursorFor(int handle) => handle switch
    {
        0 or 4 => WF.Cursors.SizeNWSE,
        2 or 6 => WF.Cursors.SizeNESW,
        1 or 5 => WF.Cursors.SizeNS,
        3 or 7 => WF.Cursors.SizeWE,
        _ => null,
    };

    /// <summary>
    /// Draws the handles on <paramref name="localRect"/>. <paramref name="scale"/> is the
    /// pop-in growth (it may overshoot past 1, which is the point), and
    /// <paramref name="focusHandle"/> gets a soft accent ring so a grabbed handle says so.
    /// </summary>
    public static void Draw(SD.Graphics g, SD.Rectangle localRect, double scale, int focusHandle)
    {
        var previous = g.SmoothingMode;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        int half = Math.Max(1, (int)Math.Round(Half * Math.Clamp(scale, 0.2d, 1.3d)));

        using var shadow = new SD.SolidBrush(SD.Color.FromArgb(60, 0, 0, 0));
        using var fill = new SD.SolidBrush(SD.Color.White);
        using var pen = new SD.Pen(ThemePalette.Accent, 1.5f);
        using var focus = new SD.SolidBrush(SD.Color.FromArgb(70, ThemePalette.Accent));

        SD.Point[] points = Points(localRect);
        for (int i = 0; i < points.Length; i++)
        {
            var dot = new SD.Rectangle(points[i].X - half, points[i].Y - half, half * 2, half * 2);
            if (i == focusHandle)
            {
                var ring = dot;
                ring.Inflate(half + 3, half + 3);
                g.FillEllipse(focus, ring);
            }
            var under = dot;
            under.Offset(0, 1);
            g.FillEllipse(shadow, under);
            g.FillEllipse(fill, dot);
            g.DrawEllipse(pen, dot);
        }
        g.SmoothingMode = previous;
    }
}
