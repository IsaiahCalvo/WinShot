using System.IO;

namespace WinShot.Recording;

internal static class RecordingFailureMessage
{
    internal static string ForFinalization(string saveFolder, string tempPath, Exception error) =>
        "WinShot couldn't save the finished recording to:\n" +
        $"{saveFolder}\n\n" +
        "Your recording is still safe here:\n" +
        $"{tempPath}\n\n" +
        "Check that the save folder is available and has free space, then move the temp file manually or try recovery after fixing the folder.\n\n" +
        $"Details: {FriendlyDetail(error)}";

    internal static string ForRecorderFailure(string? tempPath, string? detail)
    {
        string recovery = !string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath)
            ? $" WinShot kept any recoverable recording data here:\n{tempPath}\n\n"
            : " ";
        return "The recorder stopped unexpectedly." + recovery +
               "You can retry the recording. If the temp file plays, move it to your save folder manually.\n\n" +
               $"Details: {FriendlyDetail(detail)}";
    }

    private static string FriendlyDetail(Exception error) => FriendlyDetail(error.Message);

    private static string FriendlyDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "Unknown recording error." : detail.Trim();
}
