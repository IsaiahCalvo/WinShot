using System.Diagnostics;
using System.Threading;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Holder discipline for the recording surfaces that animate. Every tween takes the
/// process-wide high-resolution clock for its life, so one that is torn down mid-flight and
/// never releases it costs battery on every machine the app runs on.
/// </summary>
[Collection("MotionRefcount")]
public class RecordingSurfaceMotionTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpUntil(Func<bool> done, int ms = 6000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static readonly SD.Rectangle OffScreenRegion = new(-6000, -6000, 400, 300);

    [Fact]
    public void DimOverlay_EntranceAndFadeOutBothReleaseTheClock()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        var overlay = new FastRecordingDimOverlay(OffScreenRegion, dimScreen: true);
        overlay.Show();
        WF.Application.DoEvents();
        overlay.FadeOutAndDispose();

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DimOverlay_DisposedMidEntranceLeavesNoClockHolder()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        for (int i = 0; i < 4; i++)
        {
            var overlay = new FastRecordingDimOverlay(OffScreenRegion, dimScreen: true);
            overlay.Show();
            WF.Application.DoEvents();
            overlay.Dispose();
        }

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void DimOverlay_FadeOutIsIdempotent()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        var overlay = new FastRecordingDimOverlay(OffScreenRegion, dimScreen: false);
        overlay.Show();
        overlay.FadeOutAndDispose();
        overlay.FadeOutAndDispose();
        overlay.Dispose();

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>With animation off system-wide the overlay must still end up gone rather than
    /// waiting for a tween that never runs.</summary>
    [Fact]
    public void DimOverlay_FadeOutStillDisposesWithAnimationOff()
    {
        using var animation = new Anim(false);

        var overlay = new FastRecordingDimOverlay(OffScreenRegion, dimScreen: true);
        overlay.Show();
        overlay.FadeOutAndDispose();

        Assert.Equal(0, Motion.Holders);
        overlay.Dispose();
    }

    /// <summary>
    /// Border-only mode ramps in like the dimmed one. Show() used to bail out before the
    /// entrance tween whenever there were no dim bands, so the red marker popped on at full
    /// alpha and then faded out on the way — an entrance and an exit that did not match.
    /// </summary>
    [Fact]
    public void BorderOnlyMarker_StartsAtZeroAndRampsUp()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        var overlay = new FastRecordingDimOverlay(MarkerRegion(), dimScreen: false);
        Assert.NotEmpty(MarkerFades(overlay)); // border-only really did build markers
        overlay.Show();

        // The frame Show() returns on, the strips are still at (or near) zero — before the fix
        // they were already at full alpha, because Show() returned before the entrance tween
        // whenever there were no dim bands to ramp.
        Assert.All(MarkerFades(overlay), fade => Assert.True(fade < 0.5, $"marker started at {fade}"));

        // ...and the ramp finishes. This assertion was dropped once on the theory that the
        // marker's breathe pulse keeps a strip below 1 forever; it does not — the pulse writes
        // _alpha and only SetFade writes the _fade these read. Without it an entrance tween
        // that died mid-flight would leave the border at partial alpha for the whole take.
        PumpUntil(() => MarkerFades(overlay).All(f => f >= 1d));
        Assert.All(MarkerFades(overlay), fade => Assert.Equal(1d, fade, 3));

        overlay.Dispose();
        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>...and the ramp resolves rather than leaving the marker invisible: with
    /// animation off Motion.Run applies the end state inline, so the strips are fully on the
    /// moment Show() returns.</summary>
    [Fact]
    public void BorderOnlyMarker_IsFullyOnImmediatelyWithAnimationOff()
    {
        using var animation = new Anim(false);

        var overlay = new FastRecordingDimOverlay(MarkerRegion(), dimScreen: false);
        overlay.Show();

        Assert.NotEmpty(MarkerFades(overlay));
        Assert.All(MarkerFades(overlay), fade => Assert.Equal(1d, fade, 3));
        overlay.Dispose();
    }

    /// <summary>Marker strips only exist where they can be laid out against the real desktop,
    /// so these take a region inside it rather than the off-screen rect the holder tests use.</summary>
    private static SD.Rectangle MarkerRegion()
    {
        SD.Rectangle desktop = CaptureService.VirtualScreen;
        return new SD.Rectangle(
            desktop.Left + (desktop.Width / 4), desktop.Top + (desktop.Height / 4),
            Math.Max(2, desktop.Width / 4), Math.Max(2, desktop.Height / 4));
    }

    /// <summary>The marker pulses on a bare timer for the whole take, so it is the one piece
    /// of motion that has to check the accessibility setting itself.</summary>
    [Fact]
    public void BorderOnlyMarker_DoesNotBreatheWhenAnimationsAreOff()
    {
        using var animation = new Anim(false);

        var overlay = new FastRecordingDimOverlay(MarkerRegion(), dimScreen: false);
        overlay.Show();
        WF.Application.DoEvents();

        Assert.NotEmpty(MarkerPulseTimers(overlay));
        Assert.All(MarkerPulseTimers(overlay), timer => Assert.False(timer.Enabled));
        overlay.Dispose();
    }

    private static IReadOnlyList<double> MarkerFades(FastRecordingDimOverlay overlay) =>
        Markers(overlay).Select(m => (double)PrivateField(m, "_fade")!).ToList();

    private static IReadOnlyList<WF.Timer> MarkerPulseTimers(FastRecordingDimOverlay overlay) =>
        Markers(overlay).Select(m => (WF.Timer)PrivateField(m, "_pulse")!).ToList();

    private static IEnumerable<object> Markers(FastRecordingDimOverlay overlay) =>
        ((System.Collections.IEnumerable)PrivateField(overlay, "_markers")!).Cast<object>();

    private static object? PrivateField(object target, string name) =>
        target.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(target);

    [Fact]
    public void CountdownWindow_ClosedMidCountdownLeavesNoClockHolder()
    {
        using var animation = new Anim(true);
        PumpUntil(() => Motion.Holders == 0);

        for (int i = 0; i < 3; i++)
        {
            var countdown = new FastRecordingCountdownWindow(3, OffScreenRegion);
            countdown.Show();
            WF.Application.DoEvents();
            countdown.Close();
            countdown.Dispose();
        }

        PumpUntil(() => Motion.Holders == 0);
        Assert.Equal(0, Motion.Holders);
    }
}
