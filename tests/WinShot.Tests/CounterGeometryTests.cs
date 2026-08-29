using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class CounterGeometryTests
{
    [Fact]
    public void Pin_ContainsBodyAndTip()
    {
        var body = new Point(20, 20);
        double r = 10;
        var g = CounterGeometry.Pin(body, r, 225);
        Assert.True(g.FillContains(body));
        double tipDist = CounterGeometry.TipDistance(r);
        var tip = new Point(
            body.X + Math.Cos(225 * Math.PI / 180) * tipDist,
            body.Y + Math.Sin(225 * Math.PI / 180) * tipDist);
        Assert.True(g.FillContains(tip) || g.StrokeContains(new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 2), tip));
        Assert.False(g.FillContains(new Point(20, 0)));
    }

    [Fact]
    public void RadiusFor_NeverBelowSurveyFloor()
    {
        Assert.True(CounterGeometry.RadiusFor(0) >= 4);
        Assert.True(CounterGeometry.RadiusFor(10) > CounterGeometry.RadiusFor(2));
    }

    [Fact]
    public void FontSize_ShrinksForLongCaptions()
    {
        Assert.True(CounterGeometry.FontSize(14, "1") > CounterGeometry.FontSize(14, "999"));
    }
}
