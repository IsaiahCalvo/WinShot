using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class MarqueeSelectionTests
{
    [Fact]
    public void DragDirectionPicksTheMode()
    {
        var start = new Point(100, 100);
        Assert.Equal(MarqueeMode.Window, MarqueeSelection.ModeFor(start, new Point(200, 150)));
        Assert.Equal(MarqueeMode.Window, MarqueeSelection.ModeFor(start, new Point(200, 50)));
        Assert.Equal(MarqueeMode.Crossing, MarqueeSelection.ModeFor(start, new Point(40, 150)));
        Assert.Equal(MarqueeMode.Crossing, MarqueeSelection.ModeFor(start, new Point(40, 50)));
    }

    [Fact]
    public void RectIsNormalisedWhicheverWayTheDragRan()
    {
        Rect forward = MarqueeSelection.RectFor(new Point(10, 10), new Point(60, 40));
        Rect backward = MarqueeSelection.RectFor(new Point(60, 40), new Point(10, 10));
        Assert.Equal(forward, backward);
        Assert.Equal(new Rect(10, 10, 50, 30), forward);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 0, true)]
    [InlineData(0, -3, true)]
    [InlineData(40, 40, true)]
    public void ShortDragsStayClicks(double dx, double dy, bool expected)
    {
        var start = new Point(100, 100);
        Assert.Equal(expected, MarqueeSelection.IsDrag(start, new Point(100 + dx, 100 + dy)));
    }

    [Fact]
    public void Window_TakesOnlyWhatIsCompletelyInside()
    {
        var marquee = new Rect(0, 0, 100, 100);
        Assert.True(MarqueeSelection.Catches(marquee, new Rect(10, 10, 40, 40), MarqueeMode.Window));

        // Straddles the right edge: left alone.
        Assert.False(MarqueeSelection.Catches(marquee, new Rect(80, 10, 40, 40), MarqueeMode.Window));
        // Entirely outside.
        Assert.False(MarqueeSelection.Catches(marquee, new Rect(200, 200, 10, 10), MarqueeMode.Window));
    }

    [Fact]
    public void Crossing_TakesAnythingItTouches()
    {
        var marquee = new Rect(0, 0, 100, 100);
        Assert.True(MarqueeSelection.Catches(marquee, new Rect(10, 10, 40, 40), MarqueeMode.Crossing));

        // Straddling the edge now counts — that is the whole difference.
        Assert.True(MarqueeSelection.Catches(marquee, new Rect(80, 10, 40, 40), MarqueeMode.Crossing));
        Assert.False(MarqueeSelection.Catches(marquee, new Rect(200, 200, 10, 10), MarqueeMode.Crossing));
    }

    [Fact]
    public void EmptyBoundsAreNeverCaught()
    {
        var marquee = new Rect(0, 0, 100, 100);
        Assert.False(MarqueeSelection.Catches(marquee, Rect.Empty, MarqueeMode.Window));
        Assert.False(MarqueeSelection.Catches(marquee, Rect.Empty, MarqueeMode.Crossing));
    }

    [Fact]
    public void Union_WrapsEveryMemberAndIgnoresEmpties()
    {
        Rect union = MarqueeSelection.Union(new[]
        {
            new Rect(10, 10, 20, 20),
            Rect.Empty,
            new Rect(100, 50, 30, 30),
        });
        Assert.Equal(new Rect(10, 10, 120, 70), union);
    }

    [Fact]
    public void Union_OfNothingIsEmpty()
    {
        Assert.True(MarqueeSelection.Union(Array.Empty<Rect>()).IsEmpty);
    }
}
