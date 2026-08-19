using System.Drawing;
using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class RectangleTweenTests
{
    private static readonly Rectangle From = new(100, 100, 200, 100);
    private static readonly Rectangle To = new(500, 400, 600, 300);

    [Fact]
    public void SettlesExactlyOnTheTargetAndStops()
    {
        var tween = new RectangleTween();
        tween.Retarget(From, To);
        Thread.Sleep(260); // past the 200 ms duration

        Assert.False(tween.Update());
        Assert.False(tween.IsActive);
        Assert.Equal(To, tween.Current);
    }

    [Fact]
    public void StaysBetweenTheEndpointsWhileRunning()
    {
        var tween = new RectangleTween();
        tween.Retarget(From, To);
        Assert.True(tween.Update());

        Rectangle current = tween.Current;
        Assert.InRange(current.X, From.X, To.X);
        Assert.InRange(current.Y, From.Y, To.Y);
        Assert.InRange(current.Width, From.Width, To.Width);
        Assert.InRange(current.Height, From.Height, To.Height);
    }

    [Fact]
    public void RetargetingMidFlightResumesFromWhereItIsNowNotFromTheOldOrigin()
    {
        var tween = new RectangleTween();
        tween.Retarget(From, To);
        Thread.Sleep(100); // roughly halfway
        tween.Update();
        Rectangle midFlight = tween.Current;
        Assert.NotEqual(From, midFlight);

        // A third target arrives. The glide must continue from the on-screen position; the
        // `from` argument is ignored because the highlight is no longer there.
        var third = new Rectangle(0, 0, 50, 50);
        tween.Retarget(From, third);
        Assert.Equal(midFlight, tween.Current);
    }

    [Fact]
    public void StopClearsActiveState()
    {
        var tween = new RectangleTween();
        tween.Retarget(From, To);
        tween.Stop();

        Assert.False(tween.IsActive);
        Assert.False(tween.Update());
    }
}
