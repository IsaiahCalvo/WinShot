using System.Diagnostics;
using System.Linq;
using WinShot.Core;
using WinShot.Ocr;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The affordances that turn a toast from a confirmation into a decision: an explicit
/// decline, a preference checkbox, an in-place toggle, and a dwell that waits instead of
/// counting down. Built for the QR-link result, which used to give the user 4.5 seconds, no
/// way to decline, and no way to stop being asked.
/// </summary>
public class ToastExtrasTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpFor(int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static ToastExtras LinkExtras(Action<bool>? onCheck = null) => new()
    {
        Sticky = true,
        SecondaryText = "Don't open",
        CheckboxText = "Don't detect links",
        CheckboxChanged = onCheck,
    };

    [Fact]
    public void APlainConfirmationCarriesNoExtras()
    {
        Assert.False(new ToastExtras().HasAny);
        Assert.True(LinkExtras().HasAny);
    }

    /// <summary>
    /// Every extra reaches the card as a real control, and the card grows to hold them --
    /// the button row and the checkbox used to have nowhere to go, so a taller toast simply
    /// clipped them under its own bottom edge.
    /// </summary>
    [Fact]
    public void TheLinkToastCarriesOpenDeclineAndTheSuppressionCheckbox()
    {
        using var a = new Anim(false);
        using var plain = new ToastWindow("QR code copied", "https://example.com", null, () => { });
        using var decision = new ToastWindow(
            "QR code copied", "https://example.com", null, () => { },
            ToastSeverity.Success, holdLong: false, extras: LinkExtras());

        Assert.Equal(2, decision.Controls.OfType<DarkButton>().Count());
        Assert.Single(decision.Controls.OfType<DarkCheckBox>());
        Assert.Empty(plain.Controls.OfType<DarkCheckBox>());
        Assert.True(decision.Height > plain.Height,
            $"the decision card must make room for its extras ({decision.Height} vs {plain.Height})");

        // The buttons fill from the right and never overlap or run off the card.
        DarkButton[] buttons = decision.Controls.OfType<DarkButton>().OrderBy(b => b.Left).ToArray();
        Assert.True(buttons[0].Right <= buttons[1].Left, "the two buttons overlap");
        Assert.True(buttons[1].Right <= decision.ClientSize.Width, "a button runs off the card");
        DarkCheckBox check = decision.Controls.OfType<DarkCheckBox>().Single();
        Assert.True(check.Top >= buttons[0].Bottom, "the checkbox sits under the buttons");
    }

    /// <summary>Ticking the box is how the user says "stop asking"; it has to reach the
    /// caller, which writes the setting.</summary>
    [Fact]
    public void TickingTheCheckbox_ReportsTheNewPreference()
    {
        using var a = new Anim(false);
        bool? reported = null;
        using var toast = new ToastWindow(
            "QR code copied", "https://example.com", null, () => { },
            ToastSeverity.Success, holdLong: false, extras: LinkExtras(v => reported = v));

        DarkCheckBox check = toast.Controls.OfType<DarkCheckBox>().Single();
        check.Checked = true;

        Assert.True(reported);
    }

    /// <summary>
    /// A sticky toast waits for an answer rather than closing after 4.5 s whether or not the
    /// user had decided. It still carries a backstop -- it is a TopMost window over the
    /// user's work, so "waits" must not mean "forever".
    /// </summary>
    [Fact]
    public void ASickyToastDoesNotCountDownBehindTheDecision()
    {
        using var a = new Anim(false);
        var toast = new ToastWindow(
            "QR code copied", "https://example.com", null, () => { },
            ToastSeverity.Success, holdLong: false, extras: LinkExtras());
        toast.StartPosition = WF.FormStartPosition.Manual;
        toast.Location = new SD.Point(-20_000, -20_000);
        toast.Show();

        PumpFor(400);
        Assert.False(toast.IsDisposed);
        WF.Timer dismiss = (WF.Timer)typeof(ToastWindow)
            .GetField("_dismiss", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(toast)!;
        Assert.True(dismiss.Enabled, "a sticky toast still needs a bound on its life");
        Assert.True(dismiss.Interval >= 10_000,
            $"the sticky backstop must not read as a dwell (was {dismiss.Interval} ms)");

        toast.Dispose();
        PumpFor(50);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// The line-break toggle relabels itself in place and re-runs the pure formatter, so the
    /// user can look at the text and change their mind -- which is the only point at which
    /// joining is a judgeable preference.
    /// </summary>
    [Fact]
    public void TheLineBreakToggle_RelabelsItselfAndReformats()
    {
        using var a = new Anim(false);
        var lines = new List<string> { "first line", "second line" };
        bool joined = false;
        string? lastCopied = null;

        var extras = new ToastExtras
        {
            ToggleText = "Cut breaks",
            Toggle = () =>
            {
                joined = !joined;
                lastCopied = OcrTextFormatter.Format(lines, joined);
                return Task.FromResult(joined ? "Add breaks" : "Cut breaks");
            },
        };

        using var toast = new ToastWindow("Text copied to clipboard", "first line", null, null,
            ToastSeverity.Success, holdLong: false, extras: extras);

        DarkButton toggle = toast.Controls.OfType<DarkButton>().Single();
        Assert.Equal("Cut breaks", toggle.Text);

        toggle.PerformClick();
        Assert.Equal("Add breaks", toggle.Text);
        Assert.Equal("first line second line", lastCopied);
        Assert.False(toast.IsDisposed);

        toggle.PerformClick();
        Assert.Equal("Cut breaks", toggle.Text);
        Assert.Equal($"first line{Environment.NewLine}second line", lastCopied);
    }

    /// <summary>
    /// The toggle's work is a clipboard write, and a busy clipboard faults the Task. The
    /// button must not relabel itself off the back of a copy that did not happen -- the
    /// label flipping was the only evidence the user had that anything was re-copied.
    /// </summary>
    [Fact]
    public void AToggleWhoseWorkFails_DoesNotClaimItWorked()
    {
        using var a = new Anim(false);
        var extras = new ToastExtras
        {
            ToggleText = "Cut breaks",
            Toggle = () => Task.FromException<string>(new InvalidOperationException("clipboard is busy")),
        };

        using var toast = new ToastWindow("Text copied to clipboard", "first line", null, null,
            ToastSeverity.Success, holdLong: false, extras: extras);

        DarkButton toggle = toast.Controls.OfType<DarkButton>().Single();
        toggle.PerformClick();
        PumpFor(50);

        Assert.Equal("Cut breaks", toggle.Text);
        Assert.False(toast.IsDisposed);
    }

    /// <summary>
    /// A toast with a control on it is a decision, and 1.7 s is not long enough to read a
    /// preview, decide and hit a 96 DIP button. The dwell rule used to name the Open button
    /// alone, because it was the only control a toast could carry when it was written.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AToastCarryingAControl_HoldsTheScreenLongEnoughToUseIt(bool withToggle, bool withSecondary)
    {
        using var a = new Anim(false);
        var extras = new ToastExtras
        {
            ToggleText = withToggle ? "Cut breaks" : null,
            Toggle = withToggle ? () => Task.FromResult("Add breaks") : null,
            SecondaryText = withSecondary ? "Don't open" : null,
        };

        using var withControl = new ToastWindow("Text copied to clipboard", "first line", null, null,
            ToastSeverity.Success, holdLong: false, extras: extras);
        using var plain = new ToastWindow("Text copied to clipboard", "first line", null, null,
            ToastSeverity.Success, holdLong: false);

        Assert.True(withControl.DismissIntervalMs > plain.DismissIntervalMs,
            $"{withControl.DismissIntervalMs} ms is no longer than the {plain.DismissIntervalMs} ms a plain confirmation gets.");
    }

    /// <summary>The raw recognizer lines have to survive the run, or there is nothing to
    /// re-format after the fact.</summary>
    [Fact]
    public void TheOcrResultKeepsItsRawLines()
    {
        var result = new OcrCaptureResult("a b", new List<string>(), new List<string> { "a", "b" });
        Assert.Equal(2, result.Lines.Count);

        // The two-argument shape older call sites use still compiles and reports no lines,
        // so a barcode-only result never offers a toggle that would do nothing.
        var codeOnly = new OcrCaptureResult(string.Empty, new List<string> { "https://example.com" });
        Assert.Empty(codeOnly.Lines);
    }
}
