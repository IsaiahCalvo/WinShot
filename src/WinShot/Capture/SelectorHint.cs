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
        sessionsSoFar < 0 ? 1 : Math.Min(sessionsSoFar + 1, SessionsShown);

    /// <summary>The sentence for the mode the selector is currently in.</summary>
    public static string Text(bool windowMode) => windowMode
        ? "Click a window to capture it. Space goes back to dragging, Esc cancels."
        : "Drag to capture. Space picks a window, Alt toggles the magnifier, Esc cancels.";
}
