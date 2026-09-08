using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>
/// Keyboard adjustment of a framed selection: arrows move it, Ctrl+arrows resize it, and
/// <c>[</c> / <c>]</c> shrink or grow every side at once. Shift makes any of them coarse.
///
/// The 1 px / 10 px idiom already existed in the annotation editor for nudging a mark; the
/// step lives here now so both surfaces mean the same thing by Shift. Deliberately unanimated
/// — this is direct manipulation, and a tween on a key-repeat would lag behind the keystrokes
/// that caused it.
/// </summary>
public static class SelectionNudge
{
    /// <summary>Pixels a bare arrow moves.</summary>
    public const int Step = 1;

    /// <summary>Pixels a shifted arrow moves.</summary>
    public const int CoarseStep = 10;

    /// <summary>The step for a keystroke, so every surface means the same by Shift.</summary>
    public static int StepFor(bool shift) => shift ? CoarseStep : Step;

    /// <summary>Whether a key does anything to a framed selection.</summary>
    public static bool Handles(WF.Keys key) => key is
        WF.Keys.Left or WF.Keys.Right or WF.Keys.Up or WF.Keys.Down or
        WF.Keys.OemOpenBrackets or WF.Keys.OemCloseBrackets;

    /// <summary>
    /// The selection after one keystroke, clipped to <paramref name="bounds"/>, or null when
    /// the key is not a nudge key. A move keeps its size (it is pushed back inside the desktop
    /// rather than clipped); a resize and a grow/shrink never fall below 1x1.
    /// </summary>
    public static SD.Rectangle? Apply(
        SD.Rectangle rect,
        WF.Keys key,
        bool shift,
        bool control,
        SD.Rectangle bounds)
    {
        int step = StepFor(shift);
        switch (key)
        {
            case WF.Keys.OemOpenBrackets:
                return Inflate(rect, -step, bounds);
            case WF.Keys.OemCloseBrackets:
                return Inflate(rect, step, bounds);
            case WF.Keys.Left:
            case WF.Keys.Right:
            case WF.Keys.Up:
            case WF.Keys.Down:
                break;
            default:
                return null;
        }

        (int dx, int dy) = key switch
        {
            WF.Keys.Left => (-step, 0),
            WF.Keys.Right => (step, 0),
            WF.Keys.Up => (0, -step),
            _ => (0, step),
        };

        if (!control)
            return SelectionHandles.Move(rect, dx, dy, bounds);

        // Ctrl resizes from the bottom-right corner, which is the edge the drag that made
        // this selection was holding.
        var resized = new SD.Rectangle(
            rect.X, rect.Y, Math.Max(1, rect.Width + dx), Math.Max(1, rect.Height + dy));
        resized.Intersect(bounds);
        return resized.Width < 1 || resized.Height < 1 ? rect : resized;
    }

    private static SD.Rectangle Inflate(SD.Rectangle rect, int amount, SD.Rectangle bounds)
    {
        var grown = SD.Rectangle.Inflate(rect, amount, amount);
        if (grown.Width < 1 || grown.Height < 1)
            return rect;
        grown.Intersect(bounds);
        return grown.Width < 1 || grown.Height < 1 ? rect : grown;
    }
}
