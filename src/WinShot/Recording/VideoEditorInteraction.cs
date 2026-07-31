using System.IO;

namespace WinShot.Recording;

/// <summary>Pure, editor-owned interaction and recovery rules kept outside the WPF window.</summary>
internal static class VideoEditorInteraction
{
    private const double TrimBoundaryEpsilonSeconds = 0.000001;
    internal const double FineTrimStepSeconds = 0.1;
    internal const double MediumTrimStepSeconds = 1;
    internal const double CoarseTrimStepSeconds = 5;

    internal static double TrimStepSeconds(bool shiftPressed, bool controlPressed) =>
        controlPressed ? CoarseTrimStepSeconds : shiftPressed ? MediumTrimStepSeconds : FineTrimStepSeconds;

    internal static VideoTrimRange AdjustTrimStart(
        double startSeconds,
        double endSeconds,
        double durationSeconds,
        double deltaSeconds)
    {
        double requested = startSeconds + deltaSeconds;
        double latestStart = Math.Max(
            0,
            endSeconds - VideoTrimRange.MinimumDurationSeconds - TrimBoundaryEpsilonSeconds);
        return VideoTrimRange.FromStart(Math.Clamp(requested, 0, latestStart), endSeconds, durationSeconds);
    }

    internal static VideoTrimRange AdjustTrimEnd(
        double startSeconds,
        double endSeconds,
        double durationSeconds,
        double deltaSeconds)
    {
        double earliestEnd = startSeconds + VideoTrimRange.MinimumDurationSeconds + TrimBoundaryEpsilonSeconds;
        double requested = Math.Clamp(endSeconds + deltaSeconds, earliestEnd, Math.Max(earliestEnd, durationSeconds));
        return VideoTrimRange.FromEnd(startSeconds, requested, durationSeconds);
    }

    internal static bool IsCancellation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException || current.HResult == unchecked((int)0x80004004))
                return true;
        }

        return false;
    }

    internal static string ExportFailureMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException =>
            "WinShot could not write beside the source video. Check folder permissions or move the source to a writable folder, then try again.",
        FileNotFoundException or DirectoryNotFoundException =>
            "The source video or its folder is no longer available. Restore the file, reopen it, and try again.",
        IOException =>
            "WinShot could not finish the output file. Check free space and make sure the source folder is available, then try again.",
        _ =>
            "WinShot could not export this video. Try a shorter trim or different quality setting. The source video was not changed.",
    };

    /// <summary>
    /// Removes an incomplete export without ever touching the source. Short retries cover the
    /// normal race where MediaComposition releases its output handle just after cancellation.
    /// </summary>
    internal static async Task<bool> TryDeletePartialOutputAsync(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return true;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(outputPath))
                    return true;

                File.Delete(outputPath);
                if (!File.Exists(outputPath))
                    return true;
            }
            catch (IOException) when (attempt < 2)
            {
                // The renderer can hold the file briefly while cancellation settles.
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                // Retry once the StorageFile handle has unwound.
            }
            catch
            {
                return false;
            }

            await Task.Delay(75).ConfigureAwait(false);
        }

        return !File.Exists(outputPath);
    }
}
