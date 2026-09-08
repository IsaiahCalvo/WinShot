using System.IO;
using WinShot.History;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The history file name is the folder's only metadata channel, so a rename has to keep the
/// classification and source-app tags and the extension while replacing the readable part.
/// </summary>
public class HistoryRenameTests
{
    private const string Dir = @"C:\history";

    [Theory]
    [InlineData("20260803-120000-000.png", "Login page", @"C:\history\Login page.png")]
    // The tags at the end survive, so filtering and the app badge keep working.
    [InlineData("20260803-120000-000-scroll.png", "Long article", @"C:\history\Long article-scroll.png")]
    [InlineData("20260803-120000-000@chrome.png", "Pricing", @"C:\history\Pricing@chrome.png")]
    [InlineData("20260803-120000-000-scroll@chrome.png", "Docs", @"C:\history\Docs-scroll@chrome.png")]
    // Typing the extension back is not an error; it just must not appear twice.
    [InlineData("20260803-120000-000.png", "Login page.png", @"C:\history\Login page.png")]
    [InlineData("20260803-120000-000.mp4", "  Demo  ", @"C:\history\Demo.mp4")]
    public void Rename_KeepsTheTagsAndTheExtension(string fileName, string requested, string expected)
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, fileName), requested, static _ => false);

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".png")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("trailing.")]
    // '@' would forge a source-app tag that no app ever produced.
    [InlineData("mail@work")]
    public void Rename_RefusesNamesThatWouldNotSurviveTheFolder(string requested)
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000.png"), requested, static _ => false);

        Assert.Null(result.Path);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // Windows would reject these itself, and File.Move throwing downgrades the good copy this
    // resolver produces to a generic "Rename failed" dialog.
    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Rename_RefusesNamesWindowsKeepsForDevices(string requested)
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000.png"), requested, static _ => false);

        Assert.Null(result.Path);
        Assert.Contains("device", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // A tag makes the stem something Windows accepts again, so the guard must not over-reach.
    [Fact]
    public void Rename_AllowsADeviceNameOnceATagFollowsIt()
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000-scroll.png"), "CON", static _ => false);

        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(Dir, "CON-scroll.png"), result.Path);
    }

    [Fact]
    public void Rename_RefusesANameThatWouldNotFitInAPath()
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000.png"), new string('x', 300), static _ => false);

        Assert.Null(result.Path);
        Assert.Contains("too long", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The prompt opens on SuggestedName, so accepting the offered default unchanged has to be a
    /// no-op. Offering the whole stem meant pressing Enter on it either failed on the '@' WinShot
    /// itself wrote, or stacked a second "-scroll" onto the name.
    /// </summary>
    [Theory]
    [InlineData("20260803-120000-000.png")]
    [InlineData("20260803-120000-000-scroll.png")]
    [InlineData("20260803-120000-000@chrome.png")]
    [InlineData("20260803-120000-000-scroll@chrome.png")]
    [InlineData("Login page@chrome.png")]
    public void TheSuggestedName_RoundTripsToTheSameFile(string fileName)
    {
        string path = Path.Combine(Dir, fileName);

        HistoryRename.Result result = HistoryRename.Resolve(
            path, HistoryRename.SuggestedName(path), static _ => true);

        Assert.Null(result.Error);
        Assert.Equal(path, result.Path);
    }

    /// <summary>A hand-placed file whose whole stem is a tag trims away to nothing, and the
    /// prompt must never open on a default it will then refuse.</summary>
    [Theory]
    [InlineData("@chrome.png")]
    [InlineData("-scroll.png")]
    [InlineData("-scroll@chrome.png")]
    [InlineData("@.png")]
    public void TheSuggestedName_IsAcceptedEvenWhenTheWholeStemIsATag(string fileName)
    {
        string path = Path.Combine(Dir, fileName);

        string suggested = HistoryRename.SuggestedName(path);

        Assert.NotEqual(string.Empty, suggested);
        HistoryRename.Result result = HistoryRename.Resolve(path, suggested, static _ => false);
        Assert.Null(result.Error);
        // ...and never on one that stacks the tag it exists to preserve: the default for
        // "-scroll.png" used to be "-scroll", which Resolve turned into "-scroll-scroll.png".
        Assert.DoesNotContain("-scroll-scroll", result.Path!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSuggestedName_IsTheReadablePartOnly()
    {
        Assert.Equal(
            "20260803-120000-000",
            HistoryRename.SuggestedName(Path.Combine(Dir, "20260803-120000-000-scroll@chrome.png")));
        Assert.Equal("Login page", HistoryRename.SuggestedName(Path.Combine(Dir, "Login page.png")));
    }

    [Fact]
    public void Rename_RefusesToOverwriteAnotherCapture()
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000.png"), "Taken", static _ => true);

        Assert.Null(result.Path);
        Assert.Contains("already in History", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void RenamingToTheSameName_IsANoOpRatherThanACollision()
    {
        string path = Path.Combine(Dir, "20260803-120000-000-scroll.png");

        HistoryRename.Result result = HistoryRename.Resolve(path, "20260803-120000-000", static _ => true);

        Assert.Null(result.Error);
        Assert.Equal(path, result.Path);
    }

    [Fact]
    public void RenamedFile_StillReadsBackItsTags()
    {
        HistoryRename.Result result = HistoryRename.Resolve(
            Path.Combine(Dir, "20260803-120000-000-scroll@chrome.png"), "Docs", static _ => false);

        var item = new HistoryItem(result.Path!);
        Assert.True(item.IsScrollingCapture);
        Assert.Equal("chrome", item.SourceApp);
    }
}
