namespace WinShot.History;

/// <summary>
/// What the grid says when it has nothing to show. "No captures yet" used to cover every case,
/// so filtering to GIFs when you own none told you that you had never taken a capture — and a
/// save folder on a disconnected drive told you your screenshots did not exist.
/// </summary>
internal readonly record struct HistoryEmptyState(
    string Glyph,
    string Title,
    string Body,
    bool ShowNewCapture,
    bool ShowRetry)
{
    // Segoe MDL2: picture, warning and lock — the same icon family the rest of the window uses.
    private const string PictureGlyph = "\uE91B";
    private const string WarningGlyph = "\uE7BA";
    private const string BlockedGlyph = "\uE72E";

    /// <summary>
    /// <paramref name="filter"/> is the active chip id ("all", "images", "scrolling", "video",
    /// "gif"). A failed load wins over everything: the grid being empty is a symptom of it, not
    /// a fact about the library.
    /// </summary>
    public static HistoryEmptyState For(string filter, bool historyEnabled, string? loadError)
    {
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            return new HistoryEmptyState(
                WarningGlyph,
                "Can't read your history folder",
                loadError!,
                ShowNewCapture: false,
                ShowRetry: true);
        }

        if (!historyEnabled)
        {
            return new HistoryEmptyState(
                BlockedGlyph,
                "History is turned off",
                "Captures are not saved here. Turn history back on under Settings, Advanced, Keep history.",
                ShowNewCapture: false,
                ShowRetry: false);
        }

        return filter switch
        {
            "images" => Filtered("No screenshots in history"),
            "scrolling" => Filtered("No scrolling captures in history"),
            "video" => Filtered("No videos in history"),
            "gif" => Filtered("No GIFs in history"),
            _ => new HistoryEmptyState(
                PictureGlyph,
                "No captures yet",
                "Screenshots and recordings you take will show up here.",
                ShowNewCapture: true,
                ShowRetry: false),
        };
    }

    private static HistoryEmptyState Filtered(string title) =>
        new(PictureGlyph, title, "Pick another filter, or take one now.", ShowNewCapture: true, ShowRetry: false);
}
