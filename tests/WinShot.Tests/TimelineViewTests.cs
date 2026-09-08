using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The trim timeline's zoom window. The mapping between seconds and the strip is the whole
/// feature, and the invariants worth pinning are the ones a drag would never reveal: the window
/// never escapes the clip, never inverts, and never collapses below something pannable.
/// </summary>
public class TimelineViewTests
{
    private const double Duration = 600; // a ten-minute take

    [Fact]
    public void Full_IsTheWholeClip()
    {
        TimelineView view = TimelineView.Full(Duration);

        Assert.Equal(0, view.StartSeconds);
        Assert.Equal(Duration, view.EndSeconds);
        Assert.True(view.IsFull(Duration));
    }

    [Fact]
    public void Mapping_RoundTripsThroughTheVisibleSlice()
    {
        var view = new TimelineView(120, 180);

        Assert.Equal(0, view.ToFraction(120), 6);
        Assert.Equal(0.5, view.ToFraction(150), 6);
        Assert.Equal(1, view.ToFraction(180), 6);
        Assert.Equal(150, view.ToSeconds(0.5), 6);
    }

    [Fact]
    public void Mapping_ClampsToTheVisibleSlice()
    {
        var view = new TimelineView(120, 180);

        Assert.Equal(0, view.ToFraction(0), 6);
        Assert.Equal(1, view.ToFraction(Duration), 6);
    }

    [Fact]
    public void Zoom_KeepsTheAnchorUnderTheSamePixel()
    {
        TimelineView view = TimelineView.Full(Duration);
        const double anchor = 400;
        double before = view.ToFraction(anchor);

        TimelineView zoomed = view.Zoomed(4, anchor, Duration);

        Assert.Equal(before, zoomed.ToFraction(anchor), 4);
        Assert.Equal(Duration / 4, zoomed.Span, 4);
    }

    [Fact]
    public void Zoom_NeverEscapesTheClip()
    {
        TimelineView view = TimelineView.Full(Duration);

        // Zoom hard at the very start, then at the very end.
        TimelineView start = view.Zoomed(50, 0, Duration);
        TimelineView end = TimelineView.Full(Duration).Zoomed(50, Duration, Duration);

        Assert.True(start.StartSeconds >= 0);
        Assert.True(end.EndSeconds <= Duration + 0.0001);
        Assert.True(start.EndSeconds > start.StartSeconds);
        Assert.True(end.EndSeconds > end.StartSeconds);
    }

    [Fact]
    public void Zoom_StopsAtAWindowYouCanStillPan()
    {
        TimelineView view = TimelineView.Full(Duration);
        for (int i = 0; i < 200; i++)
            view = view.Zoomed(TimelineView.WheelZoomFactor, 300, Duration);

        Assert.Equal(TimelineView.MinimumSpanSeconds, view.Span, 6);
    }

    [Fact]
    public void Zoom_OutCannotShowMoreThanTheClip()
    {
        TimelineView view = TimelineView.Full(Duration).Zoomed(8, 300, Duration);
        for (int i = 0; i < 200; i++)
            view = view.Zoomed(1 / TimelineView.WheelZoomFactor, 300, Duration);

        Assert.True(view.IsFull(Duration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.NaN)]
    public void Zoom_IgnoresANonsenseFactor(double factor)
    {
        var view = new TimelineView(100, 200);

        Assert.Equal(view, view.Zoomed(factor, 150, Duration));
    }

    [Fact]
    public void Pan_SlidesTheWindowWithoutChangingItsSpan()
    {
        var view = new TimelineView(100, 160);

        TimelineView panned = view.Panned(30, Duration);

        Assert.Equal(130, panned.StartSeconds, 6);
        Assert.Equal(60, panned.Span, 6);
    }

    [Fact]
    public void Pan_StopsAtBothEndsOfTheClip()
    {
        var view = new TimelineView(100, 160);

        Assert.Equal(0, view.Panned(-9999, Duration).StartSeconds, 6);
        Assert.Equal(Duration, view.Panned(9999, Duration).EndSeconds, 6);
    }

    /// <summary>A keyboard nudge that pushed a handle off the visible slice would leave it
    /// un-nudgeable, so the view follows it.</summary>
    [Fact]
    public void Containing_BringsATimeBackIntoView()
    {
        var view = new TimelineView(100, 160);

        TimelineView left = view.Containing(40, Duration);
        TimelineView right = view.Containing(400, Duration);

        Assert.InRange(40, left.StartSeconds, left.EndSeconds);
        Assert.InRange(400, right.StartSeconds, right.EndSeconds);
        Assert.Equal(60, left.Span, 6);
        Assert.Equal(60, right.Span, 6);
    }

    [Fact]
    public void Containing_LeavesAVisibleTimeAlone()
    {
        var view = new TimelineView(100, 160);

        Assert.Equal(view, view.Containing(130, Duration));
    }

    [Fact]
    public void Lerp_WalksFromOneWindowToTheOther()
    {
        var from = new TimelineView(100, 160);
        TimelineView to = TimelineView.Full(Duration);

        Assert.Equal(from, TimelineView.Lerp(from, to, 0));
        Assert.Equal(to, TimelineView.Lerp(from, to, 1));
        Assert.Equal(50, TimelineView.Lerp(from, to, 0.5).StartSeconds, 6);
    }

    [Fact]
    public void ADegenerateClipStillProducesAUsableWindow()
    {
        foreach (double duration in new[] { 0d, -5d, double.NaN })
        {
            TimelineView view = TimelineView.Full(duration);
            Assert.True(view.Span >= TimelineView.MinimumSpanSeconds);
            Assert.True(view.EndSeconds > view.StartSeconds);
        }
    }

    /// <summary>A non-finite playhead used to hand back a NaN view and take the strip with it;
    /// every other method on the type is total.</summary>
    [Fact]
    public void ANonFinitePositionLeavesTheViewAlone()
    {
        var view = new TimelineView(10, 40);

        foreach (double seconds in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(view, view.Containing(seconds, Duration));
    }
}
