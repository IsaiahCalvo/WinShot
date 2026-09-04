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
