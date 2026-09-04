using WinShot.History;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// "No captures yet" used to be the only thing an empty grid could say, so it lied in three
/// separate ways. These pin what it says instead.
/// </summary>
public class HistoryEmptyStateTests
{
    [Theory]
    [InlineData("all", "No captures yet")]
    [InlineData("images", "No screenshots in history")]
    [InlineData("scrolling", "No scrolling captures in history")]
    [InlineData("video", "No videos in history")]
    [InlineData("gif", "No GIFs in history")]
    public void EmptyGrid_NamesTheFilterItIsEmptyUnder(string filter, string expected)
    {
        HistoryEmptyState state = HistoryEmptyState.For(filter, historyEnabled: true, loadError: null);

        Assert.Equal(expected, state.Title);
        Assert.True(state.ShowNewCapture);
        Assert.False(state.ShowRetry);
    }

    [Fact]
    public void FailedLoad_SaysSoAndOffersARetryInsteadOfClaimingTheLibraryIsEmpty()
    {
        HistoryEmptyState state = HistoryEmptyState.For(
            "all", historyEnabled: true, loadError: "The drive is not ready.");

        Assert.Equal("Can't read your history folder", state.Title);
        Assert.Equal("The drive is not ready.", state.Body);
        Assert.True(state.ShowRetry);
        // Taking a new capture cannot fix an unreadable folder, so it is not offered.
        Assert.False(state.ShowNewCapture);
    }

    [Fact]
    public void FailedLoad_WinsOverEveryOtherReasonTheGridIsEmpty()
    {
        HistoryEmptyState state = HistoryEmptyState.For(
            "gif", historyEnabled: false, loadError: "Access to the path is denied.");

        Assert.Equal("Can't read your history folder", state.Title);
        Assert.True(state.ShowRetry);
    }

    [Fact]
    public void HistoryTurnedOff_ExplainsItselfAndOffersNoButtons()
    {
        HistoryEmptyState state = HistoryEmptyState.For("all", historyEnabled: false, loadError: null);

        Assert.Equal("History is turned off", state.Title);
        Assert.Contains("Settings", state.Body, StringComparison.Ordinal);
        Assert.False(state.ShowNewCapture);
        Assert.False(state.ShowRetry);
    }

    [Fact]
    public void EveryState_HasAGlyphAndABody()
    {
        foreach (HistoryEmptyState state in new[]
        {
            HistoryEmptyState.For("all", true, null),
            HistoryEmptyState.For("video", true, null),
            HistoryEmptyState.For("all", false, null),
            HistoryEmptyState.For("all", true, "boom"),
        })
        {
            Assert.False(string.IsNullOrWhiteSpace(state.Glyph));
            Assert.False(string.IsNullOrWhiteSpace(state.Title));
            Assert.False(string.IsNullOrWhiteSpace(state.Body));
        }
    }
}
