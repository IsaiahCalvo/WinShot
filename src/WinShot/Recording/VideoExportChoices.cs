using System.IO;

namespace WinShot.Recording;

/// <summary>
/// What the export buttons say and when the trim-only path is on offer.
///
/// The editor always re-encoded, even when the only thing the user had done was move the trim
/// handles — so a lossless cut cost a full transcode and a generation of quality. Trimming
/// alone can be done with the source's own encoding, which is both faster and lossless, but
/// only when there is a trim to make; hence one place that decides.
/// </summary>
public static class VideoExportLabels
{
    /// <summary>Below this the handles have not really moved — a fraction of a frame is not an
    /// edit, and offering a "trim" that changes nothing would be a lie.</summary>
    public const double TrimEpsilonSeconds = 0.05;

    public static bool HasTrim(double startSeconds, double endSeconds, double durationSeconds)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || durationSeconds <= 0)
            return false;
        return startSeconds > TrimEpsilonSeconds ||
               endSeconds < durationSeconds - TrimEpsilonSeconds;
    }

    /// <summary>The primary button stops promising a trim it is not making.</summary>
    public static string PrimaryLabel(bool hasTrim) => hasTrim ? "Trim & convert" : "Convert";

    public static string PrimaryHelp(bool hasTrim) => hasTrim
        ? "Trims and re-encodes the video at the chosen size and quality."
        : "Re-encodes the video at the chosen size and quality.";
}

/// <summary>Where an export lands.</summary>
public static class VideoExportDestination
{
    public const int BesideOriginal = 0;
    public const int SaveFolder = 1;
    public const int ChooseFolder = 2;

    /// <summary>
    /// The folder an export should be written to. Anything missing falls back to the source's
    /// own folder, which is guaranteed to exist because the file being edited is in it — an
    /// export must never fail because a save folder was moved.
    /// </summary>
    public static string Resolve(
        int destinationIndex,
        string sourcePath,
        string? saveFolder,
        string? chosenFolder,
        Func<string, bool> folderExists)
    {
        string beside = Path.GetDirectoryName(sourcePath) ?? "";
        string? wanted = destinationIndex switch
        {
            SaveFolder => saveFolder,
            ChooseFolder => chosenFolder,
            _ => beside,
        };

        return !string.IsNullOrWhiteSpace(wanted) && folderExists(wanted)
            ? wanted
            : beside;
    }

    /// <summary>
    /// The name to write under. Kept separate from the folder so "replace the original" can
    /// ask for the source's own name and get exactly it back.
    /// </summary>
    public static string EditedFileName(string sourcePath) =>
        $"{Path.GetFileNameWithoutExtension(sourcePath)} (edited){Path.GetExtension(sourcePath)}";
}
