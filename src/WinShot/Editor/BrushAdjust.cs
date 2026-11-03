namespace WinShot.Editor;

/// <summary>Which setting a brush drag has locked onto.</summary>
internal enum BrushAdjustAxis { None, Width, Opacity }

/// <summary>
/// Survey's on-canvas brush drag (utils/brushAdjustGesture.js), copied from Photoshop: hold Alt
/// and drag with the right button (or Ctrl+Alt with the left). Sideways changes the armed
/// tool's width, up and down its opacity (up = more solid).
///
/// The first clear direction locks the drag to that one setting until the button is let go, so
/// opacity can't drift while resizing. Width grows with the width — every 120 px of drag
/// doubles or halves it — because widths are small numbers where a pixel-per-point drag would
/// be far too twitchy at the low end. Let go = the values are set exactly as if picked in the
/// toolbar; Escape or losing the mouse = nothing changes.
/// </summary>
internal static class BrushAdjust
{
    /// <summary>Screen pixels of drag before the gesture picks which setting it changes.</summary>
    public const double AxisLockPx = 4;

    /// <summary>Screen pixels of sideways drag that double (or halve) the width.</summary>
    public const double DoublingPx = 120;

    /// <summary>Screen pixels of up / down drag per 1 % of opacity.</summary>
    public const double PxPerPercent = 2;

    /// <summary>The least opacity the drag sets: a 0 % stroke would draw nothing.</summary>
    public const double MinOpacity = 5;

    /// <summary>Tools that draw a stroke with a width (Survey BRUSH_ADJUST_TOOLS).</summary>
    public static bool AppliesTo(EditorTool tool) => tool is EditorTool.Freehand or EditorTool.Highlighter
        or EditorTool.Line or EditorTool.Arrow or EditorTool.CurvedArrow or EditorTool.Rectangle
        or EditorTool.Ellipse or EditorTool.Polygon or EditorTool.Polyline or EditorTool.Eraser;

    /// <summary>The eraser has a size and no opacity.</summary>
    public static bool HasOpacity(EditorTool tool) => tool != EditorTool.Eraser;

    /// <summary>
    /// The values for a drag of (<paramref name="dx"/>, <paramref name="dy"/>) screen pixels from
    /// the press. Pass back the axis the previous call returned. WinShot widths are whole numbers.
    /// </summary>
    public static (int Width, double Opacity, BrushAdjustAxis Axis) Resolve(
        double startWidth, double startOpacity, double dx, double dy, BrushAdjustAxis axis,
        int minWidth, int maxWidth, bool canOpacity)
    {
        double w0 = Math.Clamp(startWidth, minWidth, maxWidth);
        double o0 = Math.Clamp(startOpacity, 0, 100);

        BrushAdjustAxis lockAxis = axis;
        if (lockAxis == BrushAdjustAxis.None && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= AxisLockPx)
            lockAxis = !canOpacity || Math.Abs(dx) >= Math.Abs(dy) ? BrushAdjustAxis.Width : BrushAdjustAxis.Opacity;

        int width = (int)Math.Round(w0);
        if (lockAxis == BrushAdjustAxis.Width)
        {
            double raw = w0 * Math.Pow(2, dx / DoublingPx);
            width = (int)Math.Clamp(Math.Round(raw), minWidth, maxWidth);
        }

        double opacity = o0;
        if (lockAxis == BrushAdjustAxis.Opacity && canOpacity)
        {
            double raw = o0 - dy / PxPerPercent;
            opacity = Math.Clamp(Math.Round(raw), Math.Min(MinOpacity, o0), 100);
        }
        return (width, opacity, lockAxis);
    }

    /// <summary>The words under the preview dot.</summary>
    public static string Label(int width, double opacity, bool eraser) =>
        eraser
            ? $"Size {width}"
            : $"Width {width} · Opacity {Math.Round(opacity)}%";
}
