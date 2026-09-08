using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ObjectSnapTests
{
    private static readonly Rect Neighbour = new(100, 100, 80, 40);

    [Fact]
    public void NothingNearbyMeansNoCorrection()
    {
        Vector v = ObjectSnap.Adjust(new Rect(0, 0, 20, 20), new[] { new Rect(500, 500, 10, 10) });
        Assert.Equal(default, v);
    }

    [Fact]
    public void ANearMissOnAnEdgeSticks()
    {
        // Left edge at 103 with a neighbour's left edge at 100: three pixels out, inside
        // the tolerance, so it goes to 100.
        Vector v = ObjectSnap.Adjust(new Rect(103, 300, 20, 20), new[] { Neighbour });
        Assert.Equal(-3, v.X, 6);
    }

    [Fact]
    public void CentresSnapToCentres()
    {
        // Neighbour's middle is x=140. A mark 20 wide whose middle is at 142 pulls to 140.
        Vector v = ObjectSnap.Adjust(new Rect(132, 300, 20, 20), new[] { Neighbour });
        Assert.Equal(-2, v.X, 6);
    }

    [Fact]
    public void RightEdgeSnapsToLeftEdge()
    {
        // Stacking one mark against another's left side is the commonest alignment there
        // is, so the two edges have to see each other.
        Vector v = ObjectSnap.Adjust(new Rect(78, 300, 20, 20), new[] { Neighbour });
        Assert.Equal(2, v.X, 6);
    }

    [Fact]
    public void TheAxesSnapIndependently()
    {
        // One mark can align its left edge with one neighbour and its top with another;
        // an all-or-nothing snap would make the common case impossible.
        var a = new Rect(0, 0, 50, 50);
        var b = new Rect(400, 200, 50, 50);
        Vector v = ObjectSnap.Adjust(new Rect(3, 197, 50, 50), new[] { a, b });

        Assert.Equal(-3, v.X, 6);
        Assert.Equal(3, v.Y, 6);
    }

    [Fact]
    public void TheNearestGuideWinsNotTheFirstOneConsidered()
    {
        // Nine edge/centre pairs per neighbour, so the two have to be placed where their
        // closest pair is unambiguous: far's best offer is 6px, near's is 2px.
        var far = new Rect(90, 500, 4, 4);   // edges 90 / 92 / 94
        var near = new Rect(106, 500, 4, 4); // edges 106 / 108 / 110
        Vector v = ObjectSnap.Adjust(new Rect(100, 300, 4, 4), new[] { far, near });

        Assert.Equal(2, v.X, 6);
    }

    [Fact]
    public void JustOutsideTheToleranceDoesNotStick()
    {
        Vector v = ObjectSnap.Adjust(
            new Rect(100 + ObjectSnap.Tolerance + 0.5, 300, 20, 20), new[] { Neighbour });
        Assert.Equal(0, v.X, 6);
    }

    [Fact]
    public void ATighterToleranceIsHonoured()
    {
        // The caller divides by the zoom, so at 8x a 6px screen tolerance is 0.75 content
        // pixels — a snap that ignored that would feel like glue.
        Vector v = ObjectSnap.Adjust(new Rect(103, 300, 20, 20), new[] { Neighbour }, tolerance: 1);
        Assert.Equal(0, v.X, 6);
    }

    [Fact]
    public void EmptyRectsOnEitherSideAreIgnored()
    {
        Assert.Equal(default, ObjectSnap.Adjust(Rect.Empty, new[] { Neighbour }));
        Assert.Equal(default, ObjectSnap.Adjust(new Rect(103, 100, 20, 20), new[] { Rect.Empty }));
    }

    [Fact]
    public void ZeroToleranceTurnsSnappingOff()
    {
        Assert.Equal(default, ObjectSnap.Adjust(new Rect(100, 100, 20, 20), new[] { Neighbour }, tolerance: 0));
    }

    [Fact]
    public void AnAlreadyAlignedMarkIsLeftAlone()
    {
        // The correction has to be zero, not "the smallest of several zeroes plus noise":
        // a mark that is already on a guide must not creep as the drag continues.
        Vector v = ObjectSnap.Adjust(new Rect(100, 100, 80, 40), new[] { Neighbour });
        Assert.Equal(0, v.X, 9);
        Assert.Equal(0, v.Y, 9);
    }
}
