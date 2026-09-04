using System.Diagnostics;
using System.Threading.Tasks;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class MotionTests
{
    static MotionTests() => WarmTheMessagePump();

    /// <summary>
    /// The first WinForms timer in the process pays for registering its message-only window,
    /// and the first <c>DoEvents</c> pays for the loop itself — together several hundred ms on
    /// a cold hidden desktop. A tween started before that is over on its first tick. Burn the
    /// cost once here so the timing tests measure the tween and not WinForms' startup.
    /// </summary>
    private static void WarmTheMessagePump()
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

    /// <summary>
    /// The high-resolution clock is process-wide and costs power, so the refcount has to be
    /// exact: raised on the first holder, dropped only by the last, and immune to double-dispose.
    /// </summary>
    [Fact]
    public void Acquire_ReferenceCountsAndSurvivesDoubleDispose()
    {
        // Pumped rather than asserted: xunit does not fix class order, and several
        // surfaces hold a bare Acquire() for the length of an animation, so an earlier
        // class can hand this one a non-zero count with no message loop left to drain it.
        PumpUntil(() => Motion.Holders == 0);

        var outer = Motion.Acquire();
        Assert.Equal(1, Motion.Holders);

        var inner = Motion.Acquire();
        Assert.Equal(2, Motion.Holders);

        inner.Dispose();
        inner.Dispose(); // idempotent: must not decrement twice
        Assert.Equal(1, Motion.Holders);

        outer.Dispose();
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public async Task Acquire_IsSafeFromManyThreadsAtOnce()
    {
        var holds = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(Motion.Acquire)));
        Assert.Equal(64, Motion.Holders);

        await Task.WhenAll(holds.Select(h => Task.Run(h.Dispose)));
        Assert.Equal(0, Motion.Holders);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1d, 1d)]
    [InlineData(0.5d, 0.5d)]
    [InlineData(-5d, 0d)]  // clamped, never overshoots into a visible jump
    [InlineData(5d, 1d)]
    public void EaseInOutSine_IsClampedAndSymmetric(double progress, double expected)
        => Assert.Equal(expected, Motion.EaseInOutSine(progress), 6);

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1d, 1d)]
    [InlineData(-5d, 0d)]
    [InlineData(5d, 1d)]
    public void EaseOutCubic_IsClamped(double progress, double expected)
        => Assert.Equal(expected, Motion.EaseOutCubic(progress), 6);

    [Fact]
    public void EaseOutCubic_DeceleratesRatherThanRunningLinear()
        => Assert.True(Motion.EaseOutCubic(0.5d) > 0.5d);

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(1d, 1d)]
    [InlineData(-5d, 0d)]
    [InlineData(5d, 1d)]
    public void EaseOutBack_StartsAndEndsExactlyOnTheTarget(double progress, double expected)
        => Assert.Equal(expected, Motion.EaseOutBack(progress), 6);

    /// <summary>
    /// The overshoot is the whole reason this ease exists — an entrance that only decelerates
    /// is EaseOutCubic. It has to go past 1 and come back, not clamp.
    /// </summary>
    [Fact]
    public void EaseOutBack_OvershootsPastTheTargetBeforeSettling()
    {
        Assert.True(Motion.EaseOutBack(0.7d) > 1d);
        Assert.True(Motion.EaseOutBack(0.99d) < Motion.EaseOutBack(0.75d));
    }

    [Fact]
    public void Durations_AreOrderedShortestFirst()
    {
        Assert.True(Motion.QuickMs < Motion.SwapMs);
        Assert.True(Motion.SwapMs < Motion.EnterDurationMs);
        Assert.Equal(140, Motion.QuickMs);
        Assert.Equal(220, Motion.SwapMs);
        Assert.Equal(280, Motion.EnterDurationMs);
    }

    /// <summary>
    /// The contract every caller leans on: frames arrive in order, never go backwards, the
    /// last one is exactly 1.0 so nobody has to paint the end state by hand, and onDone fires
    /// once, after it. The high-resolution clock must be back down when it stops.
    /// </summary>
    [Fact]
    public void Run_DeliversRisingProgress_ThenExactlyOneFinalFrameAndOneDone()
    {
        using (Animations.ForcedOn())
        {
            var frames = new List<double>();
            int done = 0;

            using IDisposable handle = Motion.Run(300, p => p, frames.Add, () => done++);
            Assert.Equal(1, Motion.Holders);

            PumpUntil(() => done > 0);

            Assert.Equal(1, done);
            Assert.Equal(1d, frames[^1]);
            Assert.Single(frames, f => f >= 1d);
            Assert.True(frames.Count > 1, $"expected several frames, got {frames.Count}");
            Assert.Equal(frames.OrderBy(f => f), frames);
            Assert.Equal(0, Motion.Holders);
        }
    }

    [Fact]
    public void Run_PassesTheEasedPositionNotRawProgress()
    {
        using (Animations.ForcedOn())
        {
            var frames = new List<double>();
            bool done = false;

            using IDisposable handle = Motion.Run(300, Motion.EaseOutBack, frames.Add, () => done = true);
            PumpUntil(() => done);

            Assert.Contains(frames, f => f > 1d);   // only EaseOutBack produces these
            Assert.Equal(1d, frames[^1]);
            Assert.Equal(0, Motion.Holders);
        }
    }

    /// <summary>
    /// Cancelling is how every window disposes mid-flight. It must stop dead — no final frame
    /// painted over a control that is going away, no onDone — and give the clock back.
    /// </summary>
    [Fact]
    public void Run_DisposeCancelsMidFlightAndReleasesTheClock()
    {
        using (Animations.ForcedOn())
        {
            var frames = new List<double>();
            int done = 0;

            IDisposable handle = Motion.Run(4000, p => p, frames.Add, () => done++);
            Assert.Equal(1, Motion.Holders);
            PumpUntil(() => frames.Count > 0);

            handle.Dispose();
            int atCancel = frames.Count;
            Assert.Equal(0, Motion.Holders);

            handle.Dispose(); // idempotent
            PumpFor(TimeSpan.FromMilliseconds(60));

            Assert.Equal(atCancel, frames.Count);
            Assert.DoesNotContain(frames, f => f >= 1d);
            Assert.Equal(0, done);
            Assert.Equal(0, Motion.Holders);
        }
    }

    /// <summary>
    /// With animation off system-wide nothing may tick at all: the end state is applied inline
    /// and the process-wide timer resolution is never raised.
    /// </summary>
    [Fact]
    public void Run_JumpsStraightToTheEndWhenClientAreaAnimationIsOff()
    {
        using (Animations.ForcedOff())
        {
            var frames = new List<double>();
            int done = 0;

            using IDisposable handle = Motion.Run(400, Motion.EaseOutBack, frames.Add, () => done++);

            Assert.Equal(new[] { 1d }, frames);
            Assert.Equal(1, done);
            Assert.Equal(0, Motion.Holders);
        }
    }

    [Fact]
    public void Run_TreatsAZeroDurationAsAnImmediateJump()
    {
        using (Animations.ForcedOn())
        {
            var frames = new List<double>();
            using IDisposable handle = Motion.Run(0, p => p, frames.Add);

            Assert.Equal(new[] { 1d }, frames);
            Assert.Equal(0, Motion.Holders);
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert.True(condition(), "timed out waiting for the tween");
    }

    private static void PumpFor(TimeSpan span)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < span)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    /// <summary>Pins Motion's view of the system animation setting for one test.</summary>
    private sealed class Animations : IDisposable
    {
        private readonly Func<bool> _previous;

        private Animations(bool enabled)
        {
            _previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => enabled;
        }

        public static Animations ForcedOn() => new(true);

        public static Animations ForcedOff() => new(false);

        public void Dispose() => Motion.AnimationsEnabled = _previous;
    }
}
