using System.IO;
using System.Text;

namespace WinShot.Core;

/// <summary>
/// The notices that legally have to travel with the binary. <c>THIRD_PARTY_NOTICES.md</c> and
/// the full licence texts are copied next to WinShot.exe by the build, so the installer
/// carries them and the About pane can show them offline.
/// </summary>
public static class ThirdPartyNotices
{
    public const string FileName = "THIRD_PARTY_NOTICES.md";

    /// <summary>Folder holding the full licence texts (Apache-2.0, Skia's own notices).</summary>
    public static string LicensesDirectory => Path.Combine(AppContext.BaseDirectory, "licenses");

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// The reader-facing part of the notices file. The file's own closing section is written
    /// for whoever maintains WinShot — it names unfinished work and internal track IDs — so
    /// it is trimmed rather than shown to a user who opened About.
    /// </summary>
    public static string Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return "The notices file did not ship with this build. " +
                       "It is in the repository at THIRD_PARTY_NOTICES.md.";
            }
            return Trim(File.ReadAllText(FilePath, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            Log.Error("Third-party notices could not be read", ex);
            return "The notices file could not be read. The details are in the log.";
        }
    }

    /// <summary>Drops the maintainer-facing tail. Public for the test that pins it.</summary>
    internal static string Trim(string markdown)
    {
        int stillOpen = markdown.IndexOf("\n## Still open", StringComparison.Ordinal);
        if (stillOpen > 0)
            markdown = markdown[..stillOpen];
        return markdown.TrimEnd() + "\n";
    }
}
