using System.IO;
using WinShot.Core;
using WinShot.Overlay;
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
    /// <summary>
    /// The body column in device pixels, read off a real toast built the way the app builds
    /// one. Handing these helpers a DIP number is what made the suite flake: the row count is
    /// a measurement of the label's own box, and that box is scaled by the anchor monitor.
    /// </summary>
    private static int BodyColumn()
    {
        using var probe = new ToastWindow("Probe", "x", new SD.Point(10, 10), null);
        return probe.BodyColumnPx;
    }

    /// <summary>Every line ShowBalloon can send, as the user would read it.</summary>
    private static string[] Messages() => new[]
    {
        // The longest fixed string in the app, and the one whose whole value is its tail.
        "No OCR language pack is installed. Add one in Settings > Time & language.",
        // Longer still, and two clauses: the second one is the instruction, so an ellipsis
        // that eats it leaves the user told off with no way forward.
        "That area has over 250 words. Redact a smaller piece, or turn Text only off.",
        "8 shortcuts are in use by other apps — see Settings > Shortcuts.",
        FriendlyError.For(new UnauthorizedAccessException()),
        FriendlyError.For(new DirectoryNotFoundException()),
        FriendlyError.For(new PathTooLongException()),
        FriendlyError.For(new IOException()),
        FriendlyError.For(new System.Net.Http.HttpRequestException()),
        FriendlyError.For(new Exception()),
        // The busy clipboard has one sentence across every surface now, so the OCR-only
        // wording that named the state without saying what to do about it is gone.
        FriendlyError.For(new ClipboardBusyException(null)),
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
        var tooTall = new List<string>();
        foreach (string m in Messages())
        {
            using var toast = new ToastWindow(
                "Save failed", m, new SD.Point(10, 10), null, ToastSeverity.Error);

            // glyph, title, body — in the order they are added.
            WF.Label body = toast.Controls.OfType<WF.Label>().Last();
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
        Assert.Equal(1, ToastWindow.LinesNeeded("shot.png", BodyColumn()));
    }

    /// <summary>
    /// Nothing the app can send should reach the three-row ceiling — at the ceiling the next
    /// word is gone, and every one of these lines ends on the instruction that makes it useful.
    /// This is also the DPI canary: the row count used to measure a pixel-unit probe font
    /// against a column scaled by GDI+'s screen DpiX, and those two never agree — a plain
    /// two-line error landed on the clamp and this failed whenever an earlier test had
    /// already made the process DPI-aware. It now measures the label's own font in the
    /// label's own pixel column, which cannot disagree with itself.
    /// </summary>
    [Fact]
    public void NoMessageNeedsMoreRowsThanTheToastGivesIt()
    {
        int column = BodyColumn();
        foreach (string m in Messages())
            Assert.True(ToastWindow.LinesNeeded(m, column) < 3, m);
    }

    /// <summary>
    /// The cap used to be a flat 90 characters, which sat inside the box and cut the OCR
    /// message at "Time &amp; Language…" — losing the half that names the Settings page.
    /// Nothing the app sends may come back shortened.
    /// </summary>
    [Fact]
    public void TheToastShowsEveryMessageWhole()
    {
        int column = BodyColumn();
        foreach (string m in Messages())
            Assert.Equal(m, ToastWindow.Trim(m, column));
    }

    /// <summary>But a message that genuinely will not fit still gets cut on a word, not
    /// mid-token, so what survives reads as a phrase.</summary>
    [Fact]
    public void AnOversizeMessageIsCutOnAWordBoundary()
    {
        string huge = string.Join(" ", Enumerable.Repeat("overflowing", 40));
        int column = BodyColumn();
        string cut = ToastWindow.Trim(huge, column);

        Assert.EndsWith("…", cut);
        Assert.True(cut.Length < huge.Length, "an oversize message was not cut at all");
        Assert.StartsWith("overflowing", cut);
        Assert.DoesNotContain("overflowin…", cut);
        Assert.True(ToastWindow.LinesNeeded(cut, column) <= 3, cut);
    }

    /// <summary>
    /// The action failed, and what the toast has to say about it is a two-row friendly error
    /// arriving in a box that was sized for a one-line URL preview. Assigning only the text
    /// left the rest of it under the label's bottom edge — the same defect the construction
    /// path already closed.
    /// </summary>
    [Fact]
    public void AFailedToastActionGrowsTheCardToHoldWhatItSays()
    {
        using var toast = new ToastWindow(
            "Text found", "example.com", new SD.Point(400, 400),
            () => throw new UnauthorizedAccessException(), ToastSeverity.Success);

        WF.Label body = toast.Controls.OfType<WF.Label>().Last();
        int before = toast.ClientSize.Height;

        toast.Controls.OfType<DarkButton>().Single().PerformClick();

        Assert.Equal(FriendlyError.For(new UnauthorizedAccessException()), body.Text);
        Assert.True(
            toast.ClientSize.Height > before,
            $"the card stayed {toast.ClientSize.Height}px for a message that needs more rows");

        SD.Size wrapped = WF.TextRenderer.MeasureText(
            body.Text, body.Font, new SD.Size(body.Width, int.MaxValue),
            WF.TextFormatFlags.WordBreak | WF.TextFormatFlags.NoPrefix);
        Assert.True(
            wrapped.Height <= body.Height,
            $"{wrapped.Width}x{wrapped.Height} in {body.Width}x{body.Height}");
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

    /// <summary>...but HttpClient reports a request timeout as a TaskCanceledException
    /// wrapping a TimeoutException, and a user who waited through a network stall on
    /// "Check for updates…" was told they had cancelled it.</summary>
    [Fact]
    public void AnHttpTimeoutIsNotReportedAsACancel()
    {
        string message = FriendlyError.For(
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException()));

        Assert.Contains("too long", message);
        Assert.DoesNotContain("cancelled", message);
    }

    /// <summary>
    /// The GDI+ arm matched the base types, so every Win32Exception (a Process.Start on a
    /// deleted exe) and every ArgumentNullException anywhere in the app was reported to the
    /// user as "The image could not be processed" on paths with no image in them.
    /// </summary>
    [Theory]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    public void AFailureWithNoImageInItIsNotCalledAnImageFailure(Type type)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;
        Assert.DoesNotContain("image", FriendlyError.For(ex), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Something went wrong. The details are in the log.", FriendlyError.For(ex));
    }

    /// <summary>The GDI+ cases the arm is actually for still map.</summary>
    [Fact]
    public void AGdiPlusFailureStillReadsAsAnImageFailure()
    {
        Assert.Contains("image", FriendlyError.For(new ArgumentException("Parameter is not valid.")));
        Assert.Contains("image", FriendlyError.For(
            new System.Runtime.InteropServices.ExternalException("A generic error occurred in GDI+.")));
    }

    /// <summary>
    /// A busy clipboard is the one failure with an answer better than "check the log", and none
    /// of the three shapes the copy paths actually throw used to reach that line. The image copy
    /// is hand-rolled Win32, so its give-up threw a Win32Exception whose own message was
    /// "Clipboard is busy." and the user read "Something went wrong"; the file copy threw a bare
    /// ExternalException and was told to retake a capture that was fine. Only SetText's
    /// COMException landed. All three are the same condition and now say the same thing.
    /// </summary>
    [Fact]
    public void EveryShapeOfABusyClipboardSaysTheClipboardIsBusy()
    {
        const string expected = "Another app is holding the clipboard. Try again in a moment.";
        const int clipboardCantOpen = unchecked((int)0x800401D0);

        // CaptureService.CopyToClipboard's give-up, wrapping the last Win32 error for the log.
        var busy = new ClipboardBusyException(
            new System.ComponentModel.Win32Exception(1418, "OpenClipboard failed"));
        Assert.Equal(expected, FriendlyError.For(busy));
        Assert.NotNull(busy.InnerException);

        // Clipboard.SetFileDropList (the card's "copy file", History, a finished recording).
        Assert.Equal(expected, FriendlyError.For(new System.Runtime.InteropServices.ExternalException(
            "Requested Clipboard operation did not succeed.", clipboardCantOpen)));

        // Clipboard.SetText (the OCR path).
        Assert.Equal(expected, FriendlyError.For(new System.Runtime.InteropServices.COMException(
            "OpenClipboard Failed", clipboardCantOpen)));
    }

    /// <summary>
    /// ...and the reason the mapper keys on the HRESULT rather than on the type: a bare
    /// Win32Exception is a failed Process.Start as often as a clipboard, so it must not be
    /// read as one just because its native code happens to be ERROR_ACCESS_DENIED.
    /// </summary>
    [Fact]
    public void AFailedProcessStartIsStillNotAClipboardFailure()
    {
        string message = FriendlyError.For(new System.ComponentModel.Win32Exception(5, "CreateProcess failed"));
        Assert.DoesNotContain("clipboard", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Trim walks the message one word at a time and re-measures the whole remainder each
    /// pass, so its cost is quadratic. Nothing produces a huge message today, but Trim is
    /// reachable from any future caller and it runs on the UI thread.
    /// </summary>
    [Fact]
    public void TrimIsBoundedForAMessageNoOneShouldHaveSent()
    {
        string huge = string.Join(" ", Enumerable.Repeat("overflowing", 20_000));
        int column = BodyColumn();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string cut = ToastWindow.Trim(huge, column);
        clock.Stop();

        Assert.EndsWith("…", cut);
        Assert.True(ToastWindow.LinesNeeded(cut, column) <= 3, cut);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"Trim took {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The quick-actions card answers a failed copy on its own status flash rather than
    /// raising a toast over itself, which means the flash now carries whole sentences and
    /// not just the two-word OCR statuses it was built for. The flash paints into the card
    /// rect with EllipsisCharacter trimming, so a line that does not fit loses its tail —
    /// and the tail is the half that says to try again in a moment. Measured on the
    /// smallest card the size slider makes, at 100 %, which is the tightest box there is.
    /// </summary>
    [Fact]
    public void TheCardsStatusFlashHoldsEveryFailureLineACopyCanProduce()
    {
        var card = QuickAccessOverlayLayout.Calculate(new SD.Size(1920, 1080), sizePercent: 0, dpi: 96).CardBounds;
        using var font = ThemePalette.UiFont(ThemePalette.PinnedCardLabelPt);
        using var bmp = new SD.Bitmap(card.Width, card.Height);
        using var g = SD.Graphics.FromImage(bmp);

        var tooBig = new List<string>();
        foreach (string m in new[]
        {
            FriendlyError.For(new ClipboardBusyException(null)),
            FriendlyError.For(new OutOfMemoryException()),
            FriendlyError.For(new IOException()),
            FriendlyError.For(new Exception()),
            "No text found",
            "Text copied",
        })
        {
            SD.SizeF wrapped = g.MeasureString(m, font, card.Width);
            if (wrapped.Height > card.Height)
                tooBig.Add($"{wrapped.Width:0}x{wrapped.Height:0} in {card.Width}x{card.Height}: \"{m}\"");
        }

        Assert.True(tooBig.Count == 0, string.Join(" | ", tooBig));
    }
}
