namespace WinShot.Capture;

/// <summary>
/// The one line of instruction the selector shows a new user. Alt toggles the magnifier, the
/// wheel walks the element ladder, Space picks a window, Enter confirms and right-click
/// cancels — every one of those existed and none of them was discoverable, because the
/// overlay opened with a dim, a cursor, and no text at all.
///
/// It is deliberately temporary: it fades out on the first drag of a session, and stops
/// appearing once the selector has been opened <see cref="SessionsShown"/> times.
/// </summary>
public static class SelectorHint
{
    /// <summary>How many selector sessions still carry the hint.</summary>
    public const int SessionsShown = 5;

    /// <summary>Whether a session that has already been opened <paramref name="sessionsSoFar"/>
    /// times should paint the hint. Negative counts (a hand-edited settings file) are treated
    /// as none seen rather than as an infinite supply.</summary>
    public static bool ShouldShow(int sessionsSoFar) =>
        sessionsSoFar < SessionsShown;

    /// <summary>The counter to persist after opening a session, saturating at the cap so it
    /// cannot run away over the life of an install.</summary>
    public static int NextSessionCount(int sessionsSoFar) =>
        // Clamp BEFORE the add. Clamping after it meant int.MaxValue wrapped to int.MinValue
        // and Math.Min happily returned that, which ShouldShow reads as "none seen" — the
        // saturation guard handing back an infinite supply of hints instead of stopping them.
        sessionsSoFar < 0 ? 1 : Math.Min(sessionsSoFar, SessionsShown - 1) + 1;

    /// <summary>
    /// The sentence for a selection that is already framed. Everything it names only works in
    /// that state, and the state suppressed the hint — so arrow-nudge, [ and ], and A were
    /// reachable only by having read the source.
    /// </summary>
    public static string PendingText =>
        "Arrows nudge, [ and ] resize, A lines it up with what's on screen, Enter captures.";

    /// <summary>The sentence for the mode the selector is currently in.</summary>
    public static string Text(bool windowMode) => windowMode
        ? "Click a window to capture it. Space goes back to dragging, Esc cancels."
        // C is one of this track's new gestures and it has no visual footprint at all: the
        // loupe shows the hex and never says it can be taken.
        : "Drag to capture. Space picks a window, Alt toggles the magnifier, C copies the colour, Esc cancels.";
}
