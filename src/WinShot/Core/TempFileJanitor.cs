using System.IO;

namespace WinShot.Core;

public static class TempFileJanitor
{
    public static string WinShotTempDirectory => Path.Combine(Path.GetTempPath(), "WinShot");

    public static int DeleteOldFiles(
        string directory,
        DateTimeOffset now,
        TimeSpan maxAge,
        int maxFilesToDelete = 100)
    {
        if (maxFilesToDelete <= 0 || !Directory.Exists(directory))
            return 0;

        int deleted = 0;
        foreach (string file in EnumerateExpiredFiles(directory, now, maxAge))
        {
            if (deleted >= maxFilesToDelete)
                break;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
            }
        }

        return deleted;
    }

    private static IEnumerable<string> EnumerateExpiredFiles(string directory, DateTimeOffset now, TimeSpan maxAge)
    {
        var cutoff = now.Subtract(maxAge).UtcDateTime;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc < cutoff)
                .OrderBy(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
            yield return file;
    }
}
