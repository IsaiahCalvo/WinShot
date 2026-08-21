using System.Threading.Tasks;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class MotionTests
{
    /// <summary>
    /// The high-resolution clock is process-wide and costs power, so the refcount has to be
    /// exact: raised on the first holder, dropped only by the last, and immune to double-dispose.
    /// </summary>
    [Fact]
    public void Acquire_ReferenceCountsAndSurvivesDoubleDispose()
    {
        Assert.Equal(0, Motion.Holders);

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
}
