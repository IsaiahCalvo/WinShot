using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class MagnifierLayoutTests
{
    [Fact]
    public void HigherMagnificationShowsLess()
    {
        // Turning the dial up and making the bubble bigger have to be two different
        // controls, not the same one twice.
        var bubble = new Rect(0, 0, 200, 100);
        var focus = new Point(500, 400);

        Rect at2 = MagnifierLayout.SourceRegion(bubble, focus, 2);
        Rect at4 = MagnifierLayout.SourceRegion(bubble, focus, 4);

        Assert.Equal(100, at2.Width, 6);
        Assert.Equal(50, at4.Width, 6);
        Assert.True(at4.Width < at2.Width);
    }

    [Fact]
    public void TheRegionIsCentredOnWhatItIsLookingAt()
    {
        Rect r = MagnifierLayout.SourceRegion(new Rect(0, 0, 200, 100), new Point(500, 400), 2);

        Assert.Equal(500, r.Left + r.Width / 2, 6);
        Assert.Equal(400, r.Top + r.Height / 2, 6);
    }

    [Fact]
    public void ABiggerBubbleShowsMoreAtTheSameMagnification()
    {
        var focus = new Point(300, 300);
        Rect small = MagnifierLayout.SourceRegion(new Rect(0, 0, 100, 100), focus, 3);
        Rect large = MagnifierLayout.SourceRegion(new Rect(0, 0, 300, 300), focus, 3);

        Assert.True(large.Width > small.Width);
        Assert.Equal(3, large.Width / small.Width, 6);
    }

    [Fact]
    public void ADegenerateBubbleStillProducesARealRegion()
    {
        // Otherwise the first frame of a drag — before the bubble has any size — would ask
        // for a zero-pixel crop.
        Rect r = MagnifierLayout.SourceRegion(new Rect(10, 10, 0, 0), new Point(10, 10), 2);
        Assert.True(r.Width > 0 && r.Height > 0);
    }

    [Fact]
    public void ScaleIsClampedToTheDialsRange()
    {
        Assert.Equal(MagnifierLayout.MinScale, MagnifierLayout.ClampScale(0.1));
        Assert.Equal(MagnifierLayout.MaxScale, MagnifierLayout.ClampScale(1000));
        Assert.Equal(MagnifierLayout.DefaultScale, MagnifierLayout.ClampScale(double.NaN));
        Assert.Equal(MagnifierLayout.DefaultScale, MagnifierLayout.ClampScale(double.PositiveInfinity));
    }

    [Fact]
    public void ANewCalloutLooksAtItself()
    {
        // The first thing a fresh bubble shows is the pixels it is sitting on, blown up.
        Assert.Equal(new Point(60, 45), MagnifierLayout.DefaultFocus(new Rect(10, 20, 100, 50)));
    }

    [Fact]
    public void TheFocusIsKeptInsideThePicture()
    {
        var source = new Size(800, 600);

        Assert.Equal(new Point(0, 0), MagnifierLayout.ClampFocus(new Point(-50, -9), source));
        Assert.Equal(new Point(800, 600), MagnifierLayout.ClampFocus(new Point(9000, 9000), source));
        Assert.Equal(new Point(400, 300), MagnifierLayout.ClampFocus(new Point(400, 300), source));
    }

    [Fact]
    public void ClampFocusSurvivesADegenerateSource()
    {
        Assert.Equal(new Point(0, 0), MagnifierLayout.ClampFocus(new Point(5, 5), new Size(0, 0)));
    }

    [Fact]
    public void MovingTheBubbleDoesNotMoveWhatItShows()
    {
        // The bubble and the focus are two rectangles on purpose: this is the property
        // that lets a callout be parked out of the way of what it is pointing at.
        var focus = new Point(120, 90);
        Rect before = MagnifierLayout.SourceRegion(new Rect(0, 0, 80, 80), focus, 2);
        Rect after = MagnifierLayout.SourceRegion(new Rect(400, 400, 80, 80), focus, 2);

        Assert.Equal(before, after);
    }
}
