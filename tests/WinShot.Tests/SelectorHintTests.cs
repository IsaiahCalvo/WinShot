using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The first-run hint. Alt toggling the magnifier, the wheel walking the element ladder,
/// Space picking a window, Enter confirming and right-click cancelling all existed before
/// this; none of them was discoverable, because the overlay opened with no text at all.
/// </summary>
public class SelectorHintTests
{
    [Fact]
    public void TheHintStopsAppearingAfterAHandfulOfSessions()
    {
        Assert.True(SelectorHint.ShouldShow(0));
        Assert.True(SelectorHint.ShouldShow(SelectorHint.SessionsShown - 1));
        Assert.False(SelectorHint.ShouldShow(SelectorHint.SessionsShown));
        Assert.False(SelectorHint.ShouldShow(SelectorHint.SessionsShown + 100));
    }

    [Fact]
    public void TheCounterSaturatesInsteadOfRunningAwayOverTheLifeOfAnInstall()
    {
        int count = 0;
        for (int i = 0; i < 50; i++)
            count = SelectorHint.NextSessionCount(count);

        Assert.Equal(SelectorHint.SessionsShown, count);
        Assert.False(SelectorHint.ShouldShow(count));
    }

    [Fact]
    public void AHandEditedNegativeCountCountsAsNoneSeenNotAnInfiniteSupply()
    {
        Assert.True(SelectorHint.ShouldShow(-3));
        Assert.Equal(1, SelectorHint.NextSessionCount(-3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachModeNamesTheKeysThatDoSomethingInIt(bool windowMode)
    {
        string text = SelectorHint.Text(windowMode);

        Assert.Contains("Space", text, StringComparison.Ordinal);
        Assert.Contains("Esc", text, StringComparison.Ordinal);
        // Sentence case, one sentence per idea, and no trailing shout.
        Assert.EndsWith("cancels.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("!", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAreaHintNamesTheMagnifierBecauseAltIsTheLeastGuessableKey()
    {
        Assert.Contains("Alt", SelectorHint.Text(windowMode: false), StringComparison.Ordinal);
    }
}
