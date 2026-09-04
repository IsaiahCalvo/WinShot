using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>Which side of a marquee the pointer is on.</summary>
public enum MarqueeSide
{
    None,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// Clicking one edge of a framed selection to snap just that edge onto the nearest real
/// element boundary, and padding the whole selection out by a step. The handle model only
/// ever offered "drag this corner and eyeball the last three pixels"; these are the two
/// gestures that finish a rectangle without dragging anything.
/// </summary>
public static class MarqueeEdge
{
    /// <summary>How near an edge the pointer counts as being on it.</summary>
    public const int TolerancePx = 6;

    /// <summary>How far an edge will travel to find something to snap to.</summary>
    public const int ReachPx = 48;

    /// <summary>Pixels one press of the padding button adds on every side.</summary>
    public const int PaddingStepPx = 8;

    /// <summary>
    /// The side under the pointer, or None. Corners deliberately answer None: they belong to
    /// the resize handles, which are hit-tested first and would otherwise lose every grab
    /// within the edge tolerance.
    /// </summary>
    public static MarqueeSide HitTest(SD.Rectangle rect, SD.Point cursor, int tolerance = TolerancePx)
    {
        if (rect.Width < 1 || rect.Height < 1)
            return MarqueeSide.None;

        bool nearLeft = Math.Abs(cursor.X - rect.Left) <= tolerance;
        bool nearRight = Math.Abs(cursor.X - rect.Right) <= tolerance;
        bool nearTop = Math.Abs(cursor.Y - rect.Top) <= tolerance;
        bool nearBottom = Math.Abs(cursor.Y - rect.Bottom) <= tolerance;
        bool withinX = cursor.X >= rect.Left - tolerance && cursor.X <= rect.Right + tolerance;
        bool withinY = cursor.Y >= rect.Top - tolerance && cursor.Y <= rect.Bottom + tolerance;

        // A corner is near two sides at once; that is the handles' ground.
        if ((nearLeft || nearRight) && (nearTop || nearBottom))
            return MarqueeSide.None;

        if (nearLeft && withinY) return MarqueeSide.Left;
        if (nearRight && withinY) return MarqueeSide.Right;
        if (nearTop && withinX) return MarqueeSide.Top;
        if (nearBottom && withinX) return MarqueeSide.Bottom;
        return MarqueeSide.None;
    }

    /// <summary>
    /// Moves one edge onto the nearest element boundary within <paramref name="reach"/>,
    /// leaving the other three where they are. Returns the rectangle unchanged when there is
    /// nothing to snap to, or when snapping would collapse it.
    /// </summary>
    public static SD.Rectangle SnapEdge(
        SD.Rectangle rect,
        MarqueeSide side,
        int[] edgesX,
        int[] edgesY,
        int reach = ReachPx)
    {
        int[] edges = side is MarqueeSide.Left or MarqueeSide.Right ? edgesX : edgesY;
        int current = side switch
        {
            MarqueeSide.Left => rect.Left,
            MarqueeSide.Right => rect.Right,
            MarqueeSide.Top => rect.Top,
            MarqueeSide.Bottom => rect.Bottom,
            _ => int.MinValue,
        };
        if (current == int.MinValue || edges is null || edges.Length == 0)
            return rect;

        int nearest = Nearest(edges, current, reach);
        if (nearest == current)
            return rect;

        SD.Rectangle snapped = side switch
        {
            MarqueeSide.Left => SD.Rectangle.FromLTRB(nearest, rect.Top, rect.Right, rect.Bottom),
            MarqueeSide.Right => SD.Rectangle.FromLTRB(rect.Left, rect.Top, nearest, rect.Bottom),
            MarqueeSide.Top => SD.Rectangle.FromLTRB(rect.Left, nearest, rect.Right, rect.Bottom),
            _ => SD.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, nearest),
        };
        return snapped.Width < 1 || snapped.Height < 1 ? rect : snapped;
    }

    /// <summary>Grows the selection on every side, clipped to the desktop.</summary>
    public static SD.Rectangle Pad(SD.Rectangle rect, int padding, SD.Rectangle bounds)
    {
        var padded = SD.Rectangle.Inflate(rect, padding, padding);
        padded.Intersect(bounds);
        return padded.Width < 1 || padded.Height < 1 ? rect : padded;
    }

    private static int Nearest(int[] sorted, int value, int reach)
    {
        int best = value;
        int bestGap = reach + 1;
        foreach (int edge in sorted)
        {
            int gap = Math.Abs(edge - value);
            if (gap > 0 && gap < bestGap)
            {
                bestGap = gap;
                best = edge;
            }
        }
        return best;
    }
}
