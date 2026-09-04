namespace WinShot.Capture;

/// <summary>What the caller should do with the region picked in all-in-one mode.</summary>
public enum AllInOneAction
{
    Capture,
    Record,
    Ocr,
    Scroll,

    /// <summary>Leave All-In-One and open the measure overlay. Unlike the others this one
    /// carries no region: measuring is a mode, not something you do to a rectangle.</summary>
    Measure,
}
