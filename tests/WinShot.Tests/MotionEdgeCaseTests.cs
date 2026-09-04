using System.Diagnostics;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The edge cases a review round turned up around Motion.Run and the controls that pump
/// it: re-entrancy, disposal mid-flight, a frame that throws, and a control (or its whole
/// form) going away while a tween is still running. Every one of them asserts
/// Motion.Holders is back to 0 — an in-flight tween holds the process-wide
/// high-resolution timer, so a leak here costs battery on every machine that runs WinShot.
/// </summary>
public class MotionEdgeCaseTests
{
    static MotionEdgeCaseTests()
    {
        using var timer = new WF.Timer { Interval = 1 };
        timer.Start();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 250)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    // ---------------------------------------------------------------- Motion

    [Fact]
    public void ReentrantRunFromInsideOnFrame()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        int innerDone = 0, outerDone = 0;
        bool started = false;
        IDisposable? inner = null;

        using IDisposable outer = Motion.Run(200, p => p, p =>
        {
            if (!started && p > 0)
            {
                started = true;
                inner = Motion.Run(120, q => q, _ => { }, () => innerDone++);
            }
        }, () => outerDone++);

        PumpUntil(() => outerDone > 0 && innerDone > 0);
        inner?.Dispose();

        Assert.Equal(1, outerDone);
        Assert.Equal(1, innerDone);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// A tween ticks on a WinForms timer, so an exception thrown by onFrame leaves through
    /// NativeWindow's callback rather than WPF's dispatcher: rethrowing it popped a modal
    /// ThreadExceptionDialog that nobody can click on the hidden test desktop (it hung a
    /// full run past the 600s timeout). The tween cancels and logs instead.
    /// </summary>
    [Fact]
    public void ThrowingFrameCancelsTheTweenAndReleasesTheClock()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        int frames = 0;
        using IDisposable handle = Motion.Run(200, p => p, _ =>
        {
            frames++;
            throw new InvalidOperationException("boom");
        });

        PumpUntil(() => Motion.Holders == 0, 2000);

        Assert.Equal(0, Motion.Holders);
        Assert.Equal(1, frames);   // cancelled on the first throw, not re-entered every tick
    }

    /// <summary>
    /// Retargeting from inside a frame — what StateAmount does every time a hover flips
    /// direction mid-fade. The first tween must hand the clock over cleanly: one onDone
    /// each, and no holder left behind.
    /// </summary>
    [Fact]
    public void RetargetingFromInsideAFrameLeavesExactlyOneClockHolder()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        int firstDone = 0, secondDone = 0;
        IDisposable? first = null, second = null;
        first = Motion.Run(400, p => p, _ =>
        {
            if (second is not null) return;
            first!.Dispose();                       // cancel from inside our own frame
            second = Motion.Run(60, p => p, _ => { }, () => secondDone++);
            Assert.Equal(1, Motion.Holders);
        }, () => firstDone++);

        PumpUntil(() => secondDone > 0);
        second?.Dispose();
        first.Dispose();

        Assert.Equal(0, firstDone);                 // cancelled, so no final frame and no onDone
        Assert.Equal(1, secondDone);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DisposeFromInsideOnFrame()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        IDisposable? handle = null;
        int frames = 0, done = 0;
        handle = Motion.Run(500, p => p, _ =>
        {
            frames++;
            if (frames == 3) handle!.Dispose();
        }, () => done++);

        PumpFor(300);
        Assert.Equal(3, frames);
        Assert.Equal(0, done);
        Assert.Equal(0, Motion.Holders);
        handle.Dispose();
    }

    // ---------------------------------------------------------- DarkControls

    [Fact]
    public void DarkButtonDisposedMidHoverFade()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { Size = new SD.Size(200, 100) };
        var button = new DarkButton { Text = "Go", Bounds = new SD.Rectangle(10, 10, 100, 30) };
        form.Controls.Add(button);
        form.CreateControl();

        typeof(WF.Control).GetMethod("OnMouseEnter", BindingFlagsNonPublic)!
            .Invoke(button, new object?[] { EventArgs.Empty });
        Assert.Equal(1, Motion.Holders);

        PumpFor(30);
        button.Dispose();                 // disposed mid-fade
        Assert.Equal(0, Motion.Holders);

        PumpFor(120);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DarkButtonHoveredThenFormClosedMidFade()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var form = new WF.Form { Size = new SD.Size(200, 100) };
        var button = new DarkButton { Text = "Go", Bounds = new SD.Rectangle(10, 10, 100, 30) };
        form.Controls.Add(button);
        form.CreateControl();

        typeof(WF.Control).GetMethod("OnMouseEnter", BindingFlagsNonPublic)!
            .Invoke(button, new object?[] { EventArgs.Empty });
        PumpFor(20);

        form.Dispose();                   // whole window goes away mid-flight
        Assert.Equal(0, Motion.Holders);
        PumpFor(150);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DarkDropDownDisposedMidHoverFade()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var drop = new DarkDropDown { Bounds = new SD.Rectangle(0, 0, 120, 26) };
        drop.CreateControl();
        typeof(WF.Control).GetMethod("OnMouseEnter", BindingFlagsNonPublic)!
            .Invoke(drop, new object?[] { EventArgs.Empty });
        Assert.Equal(1, Motion.Holders);

        PumpFor(20);
        drop.Dispose();
        Assert.Equal(0, Motion.Holders);
        PumpFor(150);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DarkSegmentedDisposedMidPillGlide()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var seg = new DarkSegmented { Options = ["MP4", "GIF", "WEBM"], Bounds = new SD.Rectangle(0, 0, 180, 28) };
        seg.CreateControl();
        seg.SelectedIndex = 2;
        Assert.Equal(1, Motion.Holders);

        PumpFor(40);
        seg.Dispose();
        Assert.Equal(0, Motion.Holders);
        PumpFor(250);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DarkSegmentedResizedMidGlide()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var seg = new DarkSegmented { Options = ["MP4", "GIF", "WEBM"], Bounds = new SD.Rectangle(0, 0, 180, 28) };
        seg.CreateControl();
        seg.SelectedIndex = 2;
        PumpFor(40);

        seg.Size = new SD.Size(300, 28);   // resize mid-glide
        Assert.Equal(0, Motion.Holders);
        PumpFor(250);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DarkSegmentedGlideCompletesAndReleases()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var seg = new DarkSegmented { Options = ["MP4", "GIF"], Bounds = new SD.Rectangle(0, 0, 180, 28) };
        seg.CreateControl();
        seg.SelectedIndex = 1;

        PumpUntil(() => Motion.Holders == 0, 3000);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// The realistic dialog path: a segmented control's index is set while the form is
    /// being built, before anything pumps messages, and the form is thrown away.
    /// </summary>
    [Fact]
    public void DarkSegmentedSetOnANeverPumpedForm()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var form = new WF.Form();
        var seg = new DarkSegmented { Options = ["A", "B"], Bounds = new SD.Rectangle(0, 0, 100, 24) };
        form.Controls.Add(seg);
        seg.SelectedIndex = 1;            // no handle, no pump
        int duringBuild = Motion.Holders;

        form.Dispose();
        Assert.Equal(0, Motion.Holders);
        Assert.True(duringBuild <= 1, $"holders during build: {duringBuild}");
    }

    [Fact]
    public void DarkSegmentedWithAnimationsOffLandsOnTheSelectedCell()
    {
        using var _ = new Anim(false);
        Assert.Equal(0, Motion.Holders);

        using var seg = new DarkSegmented { Options = ["A", "B", "C"], Bounds = new SD.Rectangle(0, 0, 150, 26) };
        seg.CreateControl();
        seg.SelectedIndex = 2;

        Assert.Equal(0, Motion.Holders);
        // Painting must not throw and must land on cell 2's pill.
        using var bmp = new SD.Bitmap(150, 26);
        seg.DrawToBitmap(bmp, new SD.Rectangle(0, 0, 150, 26));
    }

    /// <summary>
    /// The toast anchored to a monitor above the laptop (negative Y — Isaiah's layout):
    /// it has to be born on that monitor rather than on the primary, and closing it
    /// mid-entrance has to hand the clock back even though the exit tween never finishes.
    /// </summary>
    [Fact]
    public void ToastClosedMidEntranceOnANegativeMonitorReleasesTheClock()
    {
        Assert.Equal(0, Motion.Holders);
        var anchor = new SD.Point(-1200, -600);

        var toast = new ToastWindow("Text copied", "hello world", anchor, null);
        Assert.Equal(WF.Screen.FromPoint(anchor).Bounds.Location, toast.Location);

        toast.Show();
        PumpFor(40);
        toast.Close();

        PumpUntil(() => toast.IsDisposed);
        PumpFor(200);
        Assert.True(toast.IsDisposed);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// A caller that disposes the toast instead of closing it never runs OnClosed, so the
    /// entrance tween was orphaned and kept the process-wide high-resolution clock raised
    /// until it finished on its own. Not reachable from any producer today; it is the trap
    /// the next one would fall into.
    /// </summary>
    [Fact]
    public void ToastDisposedWithoutClosingReleasesTheClockImmediately()
    {
        Assert.Equal(0, Motion.Holders);

        var toast = new ToastWindow("Saved", "shot.png", new SD.Point(80, 80), null);
        toast.Show();
        PumpFor(40);
        toast.Dispose();

        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// A failure and a confirmation have to be told apart at a glance, and an error has to
    /// hold the screen long enough to read: the shortest dwell in the app used to go to the
    /// message the user most needs.
    /// </summary>
    [Fact]
    public void ToastSeverityChangesTheGlyphAndGivesAnErrorTimeToRead()
    {
        string Glyph(ToastSeverity severity)
        {
            using var toast = new ToastWindow("t", "b", new SD.Point(10, 10), null, severity);
            return toast.Controls.OfType<WF.Label>().First().Text;
        }

        Assert.Equal(3, new[]
        {
            Glyph(ToastSeverity.Info), Glyph(ToastSeverity.Success), Glyph(ToastSeverity.Error),
        }.Distinct().Count());

        int Dwell(ToastSeverity severity)
        {
            using var toast = new ToastWindow("t", "b", new SD.Point(10, 10), null, severity);
            var timer = (WF.Timer)typeof(ToastWindow)
                .GetField("_dismiss", BindingFlagsNonPublic)!.GetValue(toast)!;
            return timer.Interval;
        }

        Assert.True(Dwell(ToastSeverity.Error) > Dwell(ToastSeverity.Success));
    }

    private const System.Reflection.BindingFlags BindingFlagsNonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
}
