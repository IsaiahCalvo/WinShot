using System.Diagnostics;
using WinShot.Capture;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>A started self-timer used to be unstoppable; Escape now cancels it and the
/// capture it was counting down to is skipped.</summary>
public class SelfTimerCancelTests
{
    [Fact]
    public void CancellingTheCountdownSkipsTheCapture()
    {
        Task<bool> run = FastSelfTimerWindow.RunAsync(5);
        var timer = WF.Application.OpenForms.OfType<FastSelfTimerWindow>().Single();
        for (int i = 0; i < 10; i++) { WF.Application.DoEvents(); Thread.Sleep(5); }
        Assert.False(run.IsCompleted);

        timer.CancelCountdown();
        var deadline = Stopwatch.StartNew();
        while (!run.IsCompleted && deadline.ElapsedMilliseconds < 3000)
        {
            WF.Application.DoEvents();
            Thread.Sleep(5);
        }

        Assert.True(run.IsCompleted, "the countdown should end as soon as it is cancelled");
        Assert.False(run.Result);
        Assert.True(timer.IsDisposed || !timer.Visible);
    }

    [Fact]
    public void NoDelayMeansGoAtOnce()
    {
        Task<bool> run = FastSelfTimerWindow.RunAsync(0);
        Assert.True(run.IsCompleted);
        Assert.True(run.Result);
    }
}
