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
