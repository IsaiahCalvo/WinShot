using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistorySearchTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("chrome", true)]
    [InlineData("CHROME", true)]
    [InlineData("chrome gif", true)]
    [InlineData("gif chrome", true)]
    [InlineData("chrome video", false)]
    [InlineData("nov 2", true)]
    [InlineData("1203", true)]
    [InlineData("cafe", true)]
    [InlineData("firefox", false)]
    public void EveryWordHasToTurnUpSomewhere(string query, bool expected)
    {
        Assert.Equal(expected, HistorySearch.Matches(
            query,
            "shot-2026-11-02-141203.gif",
            "Chrome",
            "Nov 2, 2026 · 2.1 MB",
            HistorySearch.KindWords(isImage: false, isVideo: false, isGif: true, isScrolling: false),
            "Café notes"));
    }

    [Fact]
    public void KindWordsAnswerToTheChipNames()
    {
        Assert.True(HistorySearch.Matches("screenshot", HistorySearch.KindWords(true, false, false, false)));
        Assert.True(HistorySearch.Matches("video", HistorySearch.KindWords(false, true, false, false)));
        Assert.True(HistorySearch.Matches("scrolling", HistorySearch.KindWords(true, false, false, true)));
        Assert.False(HistorySearch.Matches("video", HistorySearch.KindWords(true, false, false, false)));
    }

    [Fact]
    public void ANoMatchSearchSaysSoInsteadOfBlamingTheLibrary()
    {
        var state = HistoryEmptyState.For("all", historyEnabled: true, loadError: null, query: "invoice");
        Assert.Contains("invoice", state.Title);
        Assert.Contains("Escape", state.Body);
        Assert.False(state.ShowNewCapture);

        var narrowed = HistoryEmptyState.For("gif", historyEnabled: true, loadError: null, query: "invoice");
        Assert.Contains("All", narrowed.Body);

        // A failed load still wins: the empty grid is a symptom of it.
        var failed = HistoryEmptyState.For("all", historyEnabled: true, loadError: "Folder missing.", query: "invoice");
        Assert.Equal("Folder missing.", failed.Body);
    }
}
