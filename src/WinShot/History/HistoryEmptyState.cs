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
    bool ShowRetry,
    bool ShowSettings = false)
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
            // The one panel with room for a button used to spend it on three levels of manual
            // wayfinding ("Settings, Advanced, Keep history"). It offers the action instead, the
            // way the other two empty states do.
            return new HistoryEmptyState(
                BlockedGlyph,
                "History is turned off",
                "Captures are not saved here. Turn Keep history back on to start collecting them.",
                ShowNewCapture: false,
                ShowRetry: false,
                ShowSettings: true);
        }

        return filter switch
        {
            "images" => Filtered("No screenshots in history", canTakeOne: true),
            "scrolling" => Filtered("No scrolling captures in history", canTakeOne: false),
            "video" => Filtered("No videos in history", canTakeOne: false),
            "gif" => Filtered("No GIFs in history", canTakeOne: false),
            _ => new HistoryEmptyState(
                PictureGlyph,
                "No captures yet",
                "Screenshots and recordings you take will show up here.",
                ShowNewCapture: true,
                ShowRetry: false),
        };
    }

    /// <summary>
    /// <paramref name="canTakeOne"/> is whether the New capture button — which runs a region
    /// screenshot — can actually fill this filter. It cannot for Videos, GIFs or Scrolling: the
    /// button ran a capture and History came back to the identical empty panel, so those get the
    /// sentence without the promise.
    /// </summary>
    private static HistoryEmptyState Filtered(string title, bool canTakeOne) =>
        new(PictureGlyph,
            title,
            canTakeOne ? "Pick another filter, or take one now." : "Pick another filter to see the rest of your captures.",
            ShowNewCapture: canTakeOne,
            ShowRetry: false);
}
