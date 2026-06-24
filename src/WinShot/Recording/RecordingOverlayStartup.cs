namespace WinShot.Recording;

public interface IRecordingOverlay
{
    void Show();
    void Close();
    void SetPaused(bool paused);
}

public readonly record struct RecordingOverlayStartupResult(
    IRecordingOverlay? ClickOverlay,
    IRecordingOverlay? KeyOverlay);

public static class RecordingOverlayStartup
{
    private const string ClickFailureMessage = "Failed to show click highlights; recording will continue without them.";
    private const string KeyFailureMessage = "Failed to show keystrokes; recording will continue without them.";

    public static RecordingOverlayStartupResult Start(
        bool showClickHighlights,
        bool showKeystrokes,
        Func<IRecordingOverlay> createClickOverlay,
        Func<IRecordingOverlay> createKeyOverlay,
        Action<string, Exception> logFailure)
    {
        IRecordingOverlay? click = showClickHighlights
            ? TryStart(createClickOverlay, ClickFailureMessage, logFailure)
            : null;
        IRecordingOverlay? key = showKeystrokes
            ? TryStart(createKeyOverlay, KeyFailureMessage, logFailure)
            : null;

        return new RecordingOverlayStartupResult(click, key);
    }

    private static IRecordingOverlay? TryStart(
        Func<IRecordingOverlay> create,
        string failureMessage,
        Action<string, Exception> logFailure)
    {
        IRecordingOverlay? overlay = null;
        try
        {
            overlay = create();
            overlay.Show();
            return overlay;
        }
        catch (Exception ex)
        {
            if (overlay is not null)
            {
                try { overlay.Close(); }
                catch { }
            }

            logFailure(failureMessage, ex);
            return null;
        }
    }
}
