namespace WinShot.Recording;

public readonly record struct RecordingPauseResult(bool IsPaused, bool Changed);

public static class RecordingPauseCoordinator
{
    public static RecordingPauseResult Pause(
        bool isRecording,
        bool isStopping,
        bool isPaused,
        Action pauseRecorder,
        Action<bool> setOverlaysPaused,
        Action<string, Exception> logFailure)
    {
        if (!isRecording || isStopping || isPaused)
            return new RecordingPauseResult(isPaused, false);

        try
        {
            pauseRecorder();
        }
        catch (Exception ex)
        {
            logFailure("Failed to pause recording", ex);
            return new RecordingPauseResult(false, false);
        }

        setOverlaysPaused(true);
        return new RecordingPauseResult(true, true);
    }

    public static RecordingPauseResult Resume(
        bool isRecording,
        bool isStopping,
        bool isPaused,
        Action resumeRecorder,
        Action<bool> setOverlaysPaused,
        Action<string, Exception> logFailure)
    {
        if (!isRecording || isStopping || !isPaused)
            return new RecordingPauseResult(isPaused, false);

        try
        {
            resumeRecorder();
        }
        catch (Exception ex)
        {
            logFailure("Failed to resume recording", ex);
            return new RecordingPauseResult(true, false);
        }

        setOverlaysPaused(false);
        return new RecordingPauseResult(false, true);
    }
}
