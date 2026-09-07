using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class ArrowGestureTests
{
    private static readonly Point From = new(10, 10);
    private static readonly Point To = new(90, 40);

    [Fact]
    public void ByDefaultTheHeadLandsWhereTheDragEnded()
    {
        var (tail, head) = AnnotationFactory.ArrowEnds(From, To, inverted: false, altHeld: false);
        Assert.Equal(From, tail);
        Assert.Equal(To, head);
    }

    [Fact]
    public void ThePreferenceTurnsTheArrowRound()
    {
        var (tail, head) = AnnotationFactory.ArrowEnds(From, To, inverted: true, altHeld: false);
        Assert.Equal(To, tail);
        Assert.Equal(From, head);
    }

    [Fact]
    public void AltTurnsItRoundForOneArrow()
    {
        var (tail, head) = AnnotationFactory.ArrowEnds(From, To, inverted: false, altHeld: true);
        Assert.Equal(To, tail);
        Assert.Equal(From, head);
    }

    [Fact]
    public void AltOnTopOfThePreferenceIsTheOverrideNotADoubleNegative()
    {
        // The whole point of a per-drag key is drawing the ONE arrow that wants the other
        // order, so with the preference on, Alt puts the head back at the drag's end.
        var (tail, head) = AnnotationFactory.ArrowEnds(From, To, inverted: true, altHeld: true);
        Assert.Equal(From, tail);
        Assert.Equal(To, head);
    }

    [Fact]
    public void TheStyleCycleVisitsEveryStyleAndWraps()
    {
        var all = Enum.GetValues<ArrowStyle>();
        var seen = new List<ArrowStyle>();
        ArrowStyle at = all[0];
        for (int i = 0; i < all.Length; i++)
        {
            seen.Add(at);
            at = AnnotationFactory.NextArrowStyle(at);
        }

        Assert.Equal(all.Length, seen.Distinct().Count());
        Assert.Equal(all[0], at); // wrapped back to the start; repeated double-clicks never dead-end
    }

    [Fact]
    public void CurvedIsInTheCycleBecauseItIsARowInTheSameMenu() =>
        Assert.Contains(ArrowStyle.Curved, Enum.GetValues<ArrowStyle>());

    [Fact]
    public void AnUnknownStyleStartsTheCycleFromTheBeginning()
    {
        // A style read back from a project written by a newer build must not send the
        // cycle out of range.
        Assert.Equal(Enum.GetValues<ArrowStyle>()[0], AnnotationFactory.NextArrowStyle((ArrowStyle)999));
    }
}
