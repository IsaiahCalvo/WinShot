using System.IO;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The toast is now the app's only feedback channel, so the words it is handed have to
/// fit the box it shows them in and have to describe what actually happened.
/// </summary>
public class FeedbackCopyTests
{
    private const int BodyWidthDip = 300 - 42 - 14;

    /// <summary>Every line ShowBalloon can send, as the user would read it.</summary>
    private static string[] Messages() => new[]
    {
        FriendlyError.For(new UnauthorizedAccessException()),
        FriendlyError.For(new DirectoryNotFoundException()),
        FriendlyError.For(new PathTooLongException()),
        FriendlyError.For(new IOException()),
        FriendlyError.For(new System.Net.Http.HttpRequestException()),
        FriendlyError.For(new Exception()),
        "Text was found, but Windows clipboard is busy.",
        "No previous region yet — capture a region first.",
        "Couldn't check unfinished recordings. Temp files were not changed.",
        "WinShot couldn't reach the internet. Check your connection and try again.",
    };

    /// <summary>
    /// The friendly-error lines are 45-77 characters and the body column is 244 DIP wide,
    /// so a one-line box ellipsed away exactly the half that says what to do about it
    /// ("Choose a different folder in Settings"). The box has to hold the whole line.
    /// </summary>
    [Fact]
    public void ToastBodyBoxHoldsEveryMessageTheAppSendsIt()
    {
        string longest = FriendlyError.For(new UnauthorizedAccessException());
        using var toast = new ToastWindow(
            "Save failed", longest, new SD.Point(10, 10), null, ToastSeverity.Error);

        // glyph, title, body — in the order they are added.
        WF.Label body = toast.Controls.OfType<WF.Label>().Last();
        var tooTall = new List<string>();
        foreach (string m in Messages())
        {
            SD.Size wrapped = WF.TextRenderer.MeasureText(
                m, body.Font, new SD.Size(body.Width, int.MaxValue),
                WF.TextFormatFlags.WordBreak | WF.TextFormatFlags.NoPrefix);
            if (wrapped.Height > body.Height || wrapped.Width > body.Width)
                tooTall.Add($"{wrapped.Width}x{wrapped.Height} in {body.Width}x{body.Height}: \"{m}\"");
        }

        Assert.True(tooTall.Count == 0, string.Join(" | ", tooTall));
    }

    /// <summary>A short confirmation must not pay for the long error's second row.</summary>
    [Fact]
    public void ShortConfirmationsKeepTheOneRowToast()
    {
        using var wordy = new ToastWindow(
            "Save failed", FriendlyError.For(new UnauthorizedAccessException()),
            new SD.Point(10, 10), null, ToastSeverity.Error);
        using var brief = new ToastWindow(
            "Saved", "shot.png", new SD.Point(10, 10), null, ToastSeverity.Success);

        Assert.True(
            wordy.ClientSize.Height > brief.ClientSize.Height,
            $"wordy {wordy.ClientSize.Height}px vs brief {brief.ClientSize.Height}px");
        Assert.Equal(1, ToastWindow.LinesNeeded("shot.png", BodyWidthDip));
    }

    /// <summary>Trim's 90-character cap is the ceiling the row budget was chosen for:
    /// nothing the app can send should ever hit the clamp and get cut.</summary>
    [Fact]
    public void NoMessageNeedsMoreRowsThanTheToastGivesIt()
    {
        foreach (string m in Messages())
            Assert.True(ToastWindow.LinesNeeded(m, BodyWidthDip) < 3, m);
    }

    /// <summary>The toast replaced a tray balloon that Narrator announced; it has to name
    /// itself and its message or it is invisible to assistive tech.</summary>
    [Fact]
    public void ToastNamesItselfForAssistiveTech()
    {
        using var toast = new ToastWindow(
            "Save failed", "The drive is full.", new SD.Point(10, 10), null, ToastSeverity.Error);

        Assert.Equal("Save failed", toast.AccessibleName);
        Assert.Equal("The drive is full.", toast.AccessibleDescription);
        Assert.Equal(WF.AccessibleRole.Alert, toast.AccessibleRole);
        Assert.All(
            toast.Controls.OfType<WF.Label>(),
            l => Assert.False(string.IsNullOrWhiteSpace(l.AccessibleName)));
    }

    /// <summary>FriendlyError runs on read paths too, so a missing file must not be
    /// reported as a failed write.</summary>
    [Fact]
    public void AReadFailureIsNotDescribedAsAFailedWrite()
    {
        Assert.DoesNotContain("written", FriendlyError.For(new FileNotFoundException("f", "x.png")));
        Assert.DoesNotContain("could not be written", FriendlyError.For(new IOException()));
    }

    /// <summary>A deliberate cancel is not a timeout.</summary>
    [Fact]
    public void ACancelIsNotReportedAsATimeout()
    {
        Assert.DoesNotContain("too long", FriendlyError.For(new OperationCanceledException()));
        Assert.DoesNotContain("too long", FriendlyError.For(new TaskCanceledException()));
        Assert.Contains("too long", FriendlyError.For(new TimeoutException()));
    }
}
