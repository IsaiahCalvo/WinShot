using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// MIT and Apache-2.0 attach their obligations to the distributed binary, so these check that
/// the notices actually travel with the build rather than only living in the repository —
/// and that the maintainer-facing tail of the file never reaches a user who opens About.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string RepoRoot() => TestPaths.RepoRoot();

    [Fact]
    public void TheLicenceTextsThatMustShip_AreInTheRepository()
    {
        string licenses = Path.Combine(RepoRoot(), "licenses");
        Assert.True(File.Exists(Path.Combine(licenses, "Apache-2.0.txt")),
            "ZXing.Net ships no licence file of its own, so the Apache-2.0 text has to come from here");
        Assert.True(
            File.Exists(Path.Combine(licenses, "SkiaSharp-NativeAssets-THIRD-PARTY-NOTICES.txt")),
            "SkiaSharp's MIT wrapper does not cover the native Skia it embeds");

        string apache = File.ReadAllText(Path.Combine(licenses, "Apache-2.0.txt"));
        Assert.Contains("Apache License", apache);
        Assert.Contains("Version 2.0, January 2004", apache);
    }

    [Fact]
    public void TheBuildCarriesTheNoticesIntoItsOutput()
    {
        // The installer copies publish\WinShot\*, so "in the output" is "in the installer".
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "WinShot", "WinShot.csproj"));
        Assert.Contains("THIRD_PARTY_NOTICES.md", csproj);
        Assert.Contains(@"licenses\*.txt", csproj);
        Assert.Contains("CopyToOutputDirectory", csproj);
    }

    [Fact]
    public void TheMaintainerFacingTailIsNotShownToUsers()
    {
        const string markdown = """
            # Third-party notices

            ## Shipped packages

            Thirteen MIT, one Apache-2.0.

            ## Still open

            - Isaiah's call on the LICENSE file. Tracked under T7.
            """;

        string trimmed = ThirdPartyNotices.Trim(markdown);
        Assert.Contains("Shipped packages", trimmed);
        Assert.DoesNotContain("Still open", trimmed);
        Assert.DoesNotContain("Isaiah", trimmed);
    }

    [Fact]
    public void TrimLeavesAFileWithoutThatSectionAlone()
    {
        const string markdown = "# Third-party notices\n\n## Shipped packages\n\nAll MIT.\n";
        Assert.Contains("Shipped packages", ThirdPartyNotices.Trim(markdown));
    }

    [Fact]
    public void ReadNeverThrows_AndNeverReturnsNothing()
    {
        // The About pane renders whatever comes back; a missing file must read as an
        // explanation, not as an empty box.
        string text = ThirdPartyNotices.Read();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void TheShippedNoticesStillNameEveryLicenceFamilyWinShotCarries()
    {
        string notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD_PARTY_NOTICES.md"));
        Assert.Contains("MIT", notices);
        Assert.Contains("Apache-2.0", notices);
        Assert.Contains("SkiaSharp", notices);
        // The dead SVG Repo asset was deleted; no third-party icon art ships.
        Assert.DoesNotContain("download-window-svgrepo-com.svg` came from", notices);
    }
}
