using System.Diagnostics;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// FrameTimer replaced the 8 ms WF.Timer behind every tween and frame loop. These pin the
/// parts of WF.Timer's contract the call sites lean on: it ticks through the ordinary message
/// pump, Stop and Dispose (even from inside a tick) silence it, a timer started inside a tick
/// waits for the next frame, and one timer's throw does not starve the others.
/// </summary>
public class FrameTimerTests
{
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.ElapsedMilliseconds < timeoutMs)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static void PumpFor(int ms)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void TicksThroughTheMessagePumpUntilStopped()
    {
        using var timer = new FrameTimer();
        int ticks = 0;
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        Assert.True(timer.Enabled);

        PumpUntil(() => ticks >= 5);
        Assert.True(ticks >= 5, $"expected at least five frames, got {ticks}");

        timer.Stop();
        Assert.False(timer.Enabled);
        int atStop = ticks;
        PumpFor(80);
        Assert.Equal(atStop, ticks);
    }

    /// <summary>
    /// The display paces it, not a 10 ms timer, so on any real panel (or the fallback) it
    /// never ticks faster than a 360 Hz refresh and never stalls.
    /// </summary>
    [Fact]
    public void TicksAtADisplayLikeRate()
    {
        using var timer = new FrameTimer();
        int ticks = 0;
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        PumpUntil(() => ticks > 0);
        int start = ticks;
        PumpFor(500);
        int frames = ticks - start;
        // 10 is 20 fps: a shared CI runner busy with the rest of the suite has measured 14
        // here. A stall, the thing this guards, would show 0-2.
        Assert.InRange(frames, 10, 180);
    }

    [Fact]
    public void DisposeFromInsideItsOwnTickStopsIt()
    {
        var timer = new FrameTimer();
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (ticks == 3)
                timer.Dispose();
        };
        timer.Start();

        PumpFor(200);
        Assert.Equal(3, ticks);
        Assert.False(timer.Enabled);
        timer.Start(); // a disposed timer stays dead
        PumpFor(60);
        Assert.Equal(3, ticks);
    }

    [Fact]
    public void TimersOnOneThreadTickInTheSameFrame()
    {
        using var a = new FrameTimer();
        using var b = new FrameTimer();
        var order = new List<char>();
        a.Tick += (_, _) => order.Add('a');
        b.Tick += (_, _) => order.Add('b');
        a.Start();
        b.Start();

        PumpUntil(() => order.Count >= 10);
        a.Stop();
        b.Stop();

        // Every frame ticks both, in start order: a, b, a, b...
        for (int i = 0; i + 1 < order.Count; i += 2)
            Assert.Equal("ab", new string(new[] { order[i], order[i + 1] }));
    }

    [Fact]
    public void ATimerStartedInsideATickWaitsForTheNextFrame()
    {
        using var first = new FrameTimer();
        using var second = new FrameTimer();
        int firstTicks = 0, secondTicks = 0, firstAtSecondStart = -1;
        second.Tick += (_, _) => secondTicks++;
        first.Tick += (_, _) =>
        {
            firstTicks++;
            if (firstAtSecondStart < 0)
            {
                firstAtSecondStart = firstTicks;
                second.Start();
                Assert.Equal(0, secondTicks);
            }
        };
        first.Start();

        PumpUntil(() => secondTicks >= 2);
        Assert.True(secondTicks >= 2);
        Assert.True(firstTicks > firstAtSecondStart);
    }

    [Fact]
    public void OneThrowingTimerDoesNotStarveTheOthers()
    {
        using var broken = new FrameTimer();
        using var healthy = new FrameTimer();
        int healthyTicks = 0;
        broken.Tick += (_, _) => throw new InvalidOperationException("boom");
        healthy.Tick += (_, _) => healthyTicks++;
        broken.Start();
        healthy.Start();

        PumpUntil(() => healthyTicks >= 3);
        Assert.True(healthyTicks >= 3);
    }

    [Fact]
    public void StoppingEveryTimerLetsTheClockIdle()
    {
        var timer = new FrameTimer();
        int ticks = 0;
        timer.Tick += (_, _) => ticks++;
        timer.Start();
        PumpUntil(() => ticks > 0);
        timer.Dispose();

        Assert.Equal(0, FrameClock.ForCurrentThread().Count);
        PumpFor(60);
        Assert.Equal(0, FrameClock.ForCurrentThread().Count);
    }
}
