namespace WinShot.Scrolling;

/// <summary>Which axis a scrolling capture advances along. There is no up-front mode
/// chooser anymore (CleanShot parity): direction is auto-detected from the first movement
/// unless preset, and auto vs. manual is a live toggle on the controls bar.</summary>
public enum ScrollDirection
{
    /// <summary>Content scrolls down; the stitch grows taller.</summary>
    Vertical,

    /// <summary>Content scrolls right; the stitch grows wider.</summary>
    Horizontal,
}
