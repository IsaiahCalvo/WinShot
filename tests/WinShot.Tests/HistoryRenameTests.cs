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
