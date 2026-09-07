using System.IO;

namespace WinShot.History;

/// <summary>
/// Works out what a renamed history file should be called.
///
/// The file name is this folder's only metadata channel — "-scroll" marks a scrolling capture
/// and "@chrome" names the app it was taken over — so a rename keeps those tags and the
/// extension, and replaces only the part the user actually reads. The timestamp in front is
/// what the user is replacing, and losing it is fine: the item falls back to the file's write
/// time, which for a history file is when it was captured.
/// </summary>
internal static class HistoryRename
{
    internal readonly record struct Result(string? Path, string? Error)
    {
        public static Result Failed(string message) => new(null, message);

        public static Result Renamed(string path) => new(path, null);
    }

    public static Result Resolve(string currentPath, string requestedName, Func<string, bool>? exists = null)
    {
        Func<string, bool> fileExists = exists ?? File.Exists;
        string name = (requestedName ?? string.Empty).Trim();
        if (name.Length == 0)
            return Result.Failed("Enter a name for this capture.");

        string extension = Path.GetExtension(currentPath);
        // The user typing the extension back is not an error, it just should not appear twice.
        if (extension.Length > 0 && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length].TrimEnd();

        if (name.Length == 0)
            return Result.Failed("Enter a name for this capture.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Result.Failed("A name cannot contain \\ / : * ? \" < > or |.");
        if (name.Contains('@'))
            return Result.Failed("A name cannot contain @ — WinShot uses it to tag the app a capture came from.");
        if (name.EndsWith('.'))
            return Result.Failed("A name cannot end with a dot.");

        string stem = Path.GetFileNameWithoutExtension(currentPath);
        string tags = TagsOf(stem);
        string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;
        string candidate = Path.Combine(directory, name + tags + extension);

        // Windows rejects these two itself. Catching them here keeps every bad name on the same
        // path — a sentence in the prompt the user can act on — instead of letting File.Move
        // throw and downgrading the answer to a generic "Rename failed".
        if (ReservedDeviceNames.Contains(name + tags, StringComparer.OrdinalIgnoreCase))
            return Result.Failed("Windows reserves that name for a device. Pick another one.");
        if (candidate.Length > MaxPathLength)
            return Result.Failed("That name is too long — the file path would not fit.");

        if (string.Equals(candidate, currentPath, StringComparison.OrdinalIgnoreCase))
            return Result.Renamed(currentPath);
        if (fileExists(candidate))
            return Result.Failed("A capture with that name is already in History.");

        return Result.Renamed(candidate);
    }

    /// <summary>
    /// What the rename prompt should open with: the part of the name the user actually reads,
    /// with the tags <see cref="Resolve"/> re-appends stripped off. Accepting the offered
    /// default therefore round-trips to the current path, instead of being rejected for the '@'
    /// WinShot itself put there or stacking a second "-scroll" on the end.
    /// </summary>
    public static string SuggestedName(string currentPath)
    {
        string stem = Path.GetFileNameWithoutExtension(currentPath);
        string name = stem[..^TagsOf(stem).Length];
        // A stray '@' TagsOf did not read as a tag (a trailing one, say) would still come back
        // as an error the moment the user pressed Enter. Never offer a name Resolve refuses.
        int at = name.LastIndexOf('@');
        return at >= 0 ? name[..at] : name;
    }

    /// <summary>Longest full path Windows accepts without long-path support turned on.</summary>
    private const int MaxPathLength = 259;

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>The classification and source-app tags at the end of a stem, in that order.</summary>
    private static string TagsOf(string stem)
    {
        string tags = string.Empty;
        int at = stem.LastIndexOf('@');
        if (at > 0 && at < stem.Length - 1)
        {
            tags = stem[at..];
            stem = stem[..at];
        }

        return stem.EndsWith("-scroll", StringComparison.OrdinalIgnoreCase)
            ? "-scroll" + tags
            : tags;
    }
}
