using System.Windows;
using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Survey offers NINE arrowheads; WinShot shipped six because it was ported when Survey had
/// six. Verified against the running app 2026-09-17: None, Solid triangle, V-shape, Open
/// circle, Open triangle, Horizontal line, Diamond, Slash, Square.
///
/// Pure geometry, so this runs in a full suite without a window.
/// </summary>
public class ArrowheadParityTests
{
    private const double Thickness = 4;
    private static double HeadSize => Math.Max(8, Thickness * 3);   // 12

    private static Geometry Arrow(ArrowheadStyle head) =>
        AnnotationFactory.ArrowGeometry(
            new Point(100, 100), new Point(300, 100), Thickness, head);

    [Theory]
    [InlineData("none", ArrowheadStyle.None)]
    [InlineData("solidTriangle", ArrowheadStyle.SolidTriangle)]
    [InlineData("vShape", ArrowheadStyle.VShape)]
    [InlineData("openCircle", ArrowheadStyle.OpenCircle)]
    [InlineData("openTriangle", ArrowheadStyle.OpenTriangle)]
    [InlineData("horizontalLine", ArrowheadStyle.HorizontalLine)]
    [InlineData("diamond", ArrowheadStyle.Diamond)]
    [InlineData("slash", ArrowheadStyle.Slash)]
    [InlineData("square", ArrowheadStyle.Square)]
    public void EveryArrowheadRoundTripsThroughStorageAndDrawsSomething(string tag, ArrowheadStyle style)
    {
        Assert.Equal(style, AnnotationStyle.ParseArrowhead(tag, out _));
        Assert.Equal(tag, AnnotationStyle.ToStorageName(style));

        var g = Arrow(style);
        Assert.False(g.Bounds.IsEmpty);
        // A head puts ink across the line; a bare shaft is 200 x 0 before the pen.
        double across = g.Bounds.Height;
        if (style == ArrowheadStyle.None)
            Assert.True(across < 1, $"None drew a head: height={across}");
        else
            Assert.True(across >= HeadSize - 0.01,
                $"{style} drew no head across the line: height={across}, headSize={HeadSize}");
    }

    /// <summary>
    /// Survey's <c>lineEndingBodyInset</c> (lineRenderHelpers.js:414-423). The shaft must stop
    /// BEHIND a hollow ending, not run through it — WinShot gave the open circle no trim at
    /// all, so the line was drawn straight through the ring.
    /// </summary>
    [Theory]
    [InlineData(ArrowheadStyle.None, 0)]
    [InlineData(ArrowheadStyle.VShape, 0)]
    [InlineData(ArrowheadStyle.HorizontalLine, 0)]
    [InlineData(ArrowheadStyle.Slash, 0)]
    [InlineData(ArrowheadStyle.SolidTriangle, 4)]          // headSize/3
    [InlineData(ArrowheadStyle.OpenTriangle, 6)]           // headSize/3 + w/2
    [InlineData(ArrowheadStyle.OpenCircle, 8)]             // headSize/2 + w/2
    [InlineData(ArrowheadStyle.Diamond, 8)]
    [InlineData(ArrowheadStyle.Square, 8)]
    public void TheShaftStopsWhereSurveyStopsIt(ArrowheadStyle style, double expectedInset)
    {
        // ShaftTrim IS the rule, and it is a pure static — assert it directly rather than
        // trying to pick the shaft back out of a flattened PathGeometry whose figure order
        // is not part of any contract.
        double trim = (double)typeof(AnnotationFactory)
            .GetMethod("ShaftTrim", System.Reflection.BindingFlags.Static
                                    | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new object[] { style, HeadSize, Thickness })!;
        Assert.Equal(expectedInset, trim, 6);
    }

    /// <summary>
    /// Diamond and Square are centred ON the endpoint and span headSize, the same footprint
    /// as the open circle — so all three reach exactly headSize/2 past the tip.
    /// </summary>
    [Theory]
    [InlineData(ArrowheadStyle.OpenCircle)]
    [InlineData(ArrowheadStyle.Diamond)]
    [InlineData(ArrowheadStyle.Square)]
    public void CentredHeadsOverhangTheTipByHalfAHead(ArrowheadStyle style)
    {
        var b = Arrow(style).Bounds;
        // The head outline is WIDENED by max(2, thickness) and appended as a fill, so the
        // bounds carry half that pen beyond the geometry on every side.
        const double pen = 4;   // max(2, Thickness)
        Assert.Equal(300 + HeadSize / 2 + pen / 2, b.Right, 6);
        Assert.Equal(HeadSize + pen, b.Height, 6);
    }

    /// <summary>Slash sits 30 degrees off the perpendicular, so it is NOT axis-aligned.</summary>
    [Fact]
    public void SlashLeansThirtyDegreesOffThePerpendicular()
    {
        var b = Arrow(ArrowheadStyle.Slash).Bounds;
        const double pen = 4;
        // A bar of length headSize leaning 30 degrees off the perpendicular spans
        // headSize*sin(30) = 6 along the line and headSize*cos(30) = 10.39 across it; the
        // widening pen adds half of itself on each side of both.
        // 2 dp, not 6: the slash's ROUND CAPS are widened into beziers, and WPF's flattening
        // of those is an approximation (it lands 0.0007 over here). The polygons above have
        // no caps to approximate, so they hold to 6.
        Assert.Equal(HeadSize * Math.Cos(Math.PI / 6) + pen, b.Height, 2);
        Assert.Equal(300 + HeadSize * Math.Sin(Math.PI / 6) / 2 + pen / 2, b.Right, 2);
    }
}
