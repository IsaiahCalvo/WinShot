namespace WinShot.Scrolling;

/// <summary>How a scrolling capture advances through the content.</summary>
public enum ScrollCaptureMode
{
    /// <summary>WinShot sends wheel-scroll input and stops at the page bottom.</summary>
    Auto,

    /// <summary>
    /// The user scrolls the content themselves; WinShot only watches the region
    /// and stitches whenever downward movement is detected. Ends only on Stop,
    /// Esc, the stitched-height cap, or a generous timeout.
    /// </summary>
    Manual,
}

/// <summary>Which axis a scrolling capture advances along.</summary>
public enum ScrollDirection
{
    /// <summary>Content scrolls down; the stitch grows taller.</summary>
    Vertical,

    /// <summary>Content scrolls right; the stitch grows wider.</summary>
    Horizontal,
}

/// <summary>
/// Result of the scrolling-capture chooser: how the capture advances
/// (auto vs. manual) and along which axis.
/// </summary>
public readonly record struct ScrollCaptureChoice(ScrollCaptureMode Mode, ScrollDirection Direction);
