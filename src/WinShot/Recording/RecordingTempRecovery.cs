using System.IO;

namespace WinShot.Recording;

internal sealed record RecoverableRecordingTempFile(
    string Path,
    string Extension,
    long Length,
    DateTime LastWriteTimeUtc);

/// <summary>
/// Finds only WinShot-owned recording temp files that are old enough to no
/// longer be an active capture. App-level recovery UI can consume this later.
/// </summary>
internal static class RecordingTempRecovery
{
    internal static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromMinutes(30);

    internal static string DefaultTempDirectory =>
        Path.Combine(Path.GetTempPath(), "WinShot");

    internal static IReadOnlyList<RecoverableRecordingTempFile> Discover(
        string? tempDirectory = null,
        DateTime? utcNow = null,
        TimeSpan? minimumAge = null,
        IEnumerable<string>? excludedPaths = null)
    {
        string directory = Path.GetFullPath(tempDirectory ?? DefaultTempDirectory);
        if (!Directory.Exists(directory))
            return Array.Empty<RecoverableRecordingTempFile>();

        DateTime now = utcNow ?? DateTime.UtcNow;
        TimeSpan age = minimumAge ?? DefaultMinimumAge;
        if (age < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumAge));

        var excluded = new HashSet<string>(
            excludedPaths?.Select(Path.GetFullPath) ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var results = new List<RecoverableRecordingTempFile>();

        foreach (string path in Directory.EnumerateFiles(directory, "recording-*", SearchOption.TopDirectoryOnly))
        {
            if (excluded.Contains(Path.GetFullPath(path)) || !IsSafeRecordingTempPath(path, directory))
                continue;

            try
            {
                var info = new FileInfo(path);
                if (info.Length <= 0 || now - info.LastWriteTimeUtc < age)
                    continue;
                results.Add(new RecoverableRecordingTempFile(
                    info.FullName,
                    info.Extension.TrimStart('.').ToLowerInvariant(),
                    info.Length,
                    info.LastWriteTimeUtc));
            }
            catch (IOException)
            {
                // A file that is still being written is not a safe recovery candidate.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip unreadable files; recovery must never broaden access.
            }
        }

        return results
            .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
            .ToArray();
    }

    internal static RecordingFinalizationResult Recover(
        string tempPath,
        string saveFolder,
        string fileName,
        string? tempDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.GetFullPath(tempDirectory ?? DefaultTempDirectory);
        if (!IsSafeRecordingTempPath(tempPath, directory))
            throw new ArgumentException("The file is not a WinShot recording temp file.", nameof(tempPath));

        return RecordingFileFinalizer.FinalizeToUniqueFinalPath(
            tempPath,
            saveFolder,
            fileName,
            cancellationToken);
    }

    internal static bool IsSafeRecordingTempPath(string path, string tempDirectory)
    {
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory));
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase))
            return false;

        string extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fullPath);
        const string prefix = "recording-";
        return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(stem[prefix.Length..], "N", out _);
    }
}
