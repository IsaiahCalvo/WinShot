using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Per-action confirmation style. The rule with teeth is the one about failures: a preference
/// about confirmations must never be able to silence an error.
/// </summary>
public class NotificationStyleTests
{
    [Fact]
    public void EveryActionStartsInWinShotsOwnToast()
    {
        var s = new Settings();
        Assert.Equal(NotificationStyle.InApp, NotificationStyle.For(NotifiedAction.Ocr, s));
        Assert.Equal(NotificationStyle.InApp, NotificationStyle.For(NotifiedAction.Copy, s));
        Assert.Equal(NotificationStyle.InApp, NotificationStyle.For(NotifiedAction.Save, s));
    }

    [Fact]
    public void EachActionIsSetIndependently()
    {
        var s = new Settings
        {
            NotifyStyleOcr = NotificationStyle.System,
            NotifyStyleCopy = NotificationStyle.Off,
            NotifyStyleSave = NotificationStyle.InApp,
        };
        Assert.Equal(NotificationStyle.System, NotificationStyle.For(NotifiedAction.Ocr, s));
        Assert.Equal(NotificationStyle.Off, NotificationStyle.For(NotifiedAction.Copy, s));
        Assert.Equal(NotificationStyle.InApp, NotificationStyle.For(NotifiedAction.Save, s));
    }

    [Theory]
    [InlineData("system", "system")]
    [InlineData("SYSTEM", "system")]
    [InlineData("  off  ", "off")]
    [InlineData("in-app", "in-app")]
    // Anything a hand-edited settings file or an older build can produce lands on the
    // default rather than silencing a confirmation by accident.
    [InlineData("", "in-app")]
    [InlineData(null, "in-app")]
    [InlineData("balloon", "in-app")]
    public void AnUnrecognisedValueFallsBackToTheDefault(string? stored, string expected)
    {
        Assert.Equal(expected, NotificationStyle.Normalize(stored));
        Assert.Equal(expected, NotificationStyle.For(NotifiedAction.Save, new Settings { NotifyStyleSave = stored! }));
    }

    [Fact]
    public void TheStylesAreDistinctValues()
    {
        // Guards against a rename collapsing two options into one silently.
        Assert.Equal(
            3,
            new HashSet<string> { NotificationStyle.InApp, NotificationStyle.System, NotificationStyle.Off }.Count);
    }
}
