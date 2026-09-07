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

    /// <summary>
    /// The counter clamps BEFORE it adds. Adding first meant a hand-edited or corrupted
    /// settings.json holding int.MaxValue wrapped to int.MinValue, which ShouldShow reads as
    /// "none seen" — the runaway guard producing the runaway it exists to stop.
    /// </summary>
    [Fact]
    public void AnAbsurdStoredCountStillSaturatesInsteadOfWrapping()
    {
        Assert.Equal(SelectorHint.SessionsShown, SelectorHint.NextSessionCount(int.MaxValue));
        Assert.False(SelectorHint.ShouldShow(SelectorHint.NextSessionCount(int.MaxValue)));
    }

    /// <summary>
    /// A custom HCURSOR is drawn at its native PIXEL size — Windows never scales it the way it
    /// scales the system cursors it stands in for — so one bitmap for the process rendered
    /// two-thirds size on the 150% primary and could not follow the pointer to another monitor.
    /// </summary>
    [Fact]
    public void TheCameraCursorIsBuiltPerMonitorScaleNotOncePerProcess()
    {
        Assert.Same(SelectorCursors.Camera(1.5), SelectorCursors.Camera(1.5));
        Assert.NotSame(SelectorCursors.Camera(1.0), SelectorCursors.Camera(2.0));
        // Out-of-range junk from a failed DPI query still lands on a real cursor.
        Assert.Same(SelectorCursors.Camera(1.0), SelectorCursors.Camera(0.1));
    }
}
