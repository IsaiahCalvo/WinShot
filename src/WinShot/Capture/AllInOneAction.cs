namespace WinShot.Capture;

/// <summary>What the caller should do with the region picked in all-in-one mode.</summary>
public enum AllInOneAction
{
    Capture,
    Record,
    Ocr,
    Scroll,
    /// <summary>Frame it now, capture it after the configured self-timer delay.</summary>
    Timer,
}
