using WinShot.History;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// "No captures yet" used to be the only thing an empty grid could say, so it lied in three
/// separate ways. These pin what it says instead.
/// </summary>
public class HistoryEmptyStateTests
{
    /// <summary>The third argument is whether "New capture" is offered: the button runs a region
    /// SCREENSHOT, so under the Videos, GIFs or Scrolling chip it would take a capture and hand
    /// the user back the identical empty panel.</summary>
    [Theory]
    [InlineData("all", "No captures yet", true)]
    [InlineData("images", "No screenshots in history", true)]
    [InlineData("scrolling", "No scrolling captures in history", false)]
    [InlineData("video", "No videos in history", false)]
    [InlineData("gif", "No GIFs in history", false)]
    public void EmptyGrid_NamesTheFilterItIsEmptyUnder(string filter, string expected, bool offersACapture)
    {
        HistoryEmptyState state = HistoryEmptyState.For(filter, historyEnabled: true, loadError: null);

        Assert.Equal(expected, state.Title);
        Assert.Equal(offersACapture, state.ShowNewCapture);
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
