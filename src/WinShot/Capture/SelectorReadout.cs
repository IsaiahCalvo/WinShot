using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// The one sentence the selector reads out beside the cursor: where the pointer is before
/// a drag, and how big the selection is during one. Both the loupe's own pill and the
/// standalone crosshair pill render this, so the two can never disagree about the wording.
/// </summary>
public static class SelectorReadout
{
    /// <summary>
    /// "<c>x, y</c>" idle, "<c>w × h</c>" while a drag has an extent. A zero-area drag is
    /// still the idle case: the numbers would both read 0 and say nothing about the pointer.
    /// </summary>
    public static string Text(SD.Point cursorScreen, SD.Size dragSize) =>
        dragSize.Width > 0 || dragSize.Height > 0
            ? $"{dragSize.Width} × {dragSize.Height}"
            : $"{cursorScreen.X}, {cursorScreen.Y}";
}
