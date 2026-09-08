using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class AnnotationStyleTests
{
    [Theory]
    [InlineData(null, LineBorderStyle.Solid)]
    [InlineData("solid", LineBorderStyle.Solid)]
    [InlineData("dashed", LineBorderStyle.Dashed)]
    [InlineData("DOTTED", LineBorderStyle.Dotted)]
    [InlineData("cloud", LineBorderStyle.Cloud)]
    [InlineData("garbage", LineBorderStyle.Solid)]
    public void ParseLineStyle_RoundTripsSurveyVocabulary(string? name, LineBorderStyle expected)
    {
        Assert.Equal(expected, AnnotationStyle.ParseLineStyle(name));
    }

    [Fact]
    public void DashArray_MatchesSurveyPatterns()
    {
        Assert.Null(AnnotationStyle.DashArray(LineBorderStyle.Solid));
        Assert.Null(AnnotationStyle.DashArray(LineBorderStyle.Cloud));
        Assert.Equal(new[] { 6d, 4d }, AnnotationStyle.DashArray(LineBorderStyle.Dashed)!.ToArray());
        Assert.Equal(new[] { 2d, 4d }, AnnotationStyle.DashArray(LineBorderStyle.Dotted)!.ToArray());
    }

    [Theory]
    [InlineData("none", ArrowheadStyle.None, ArrowheadStyle.None)]
    [InlineData("solidTriangle", ArrowheadStyle.SolidTriangle, ArrowheadStyle.None)]
    [InlineData("vShape", ArrowheadStyle.VShape, ArrowheadStyle.None)]
    [InlineData("openCircle", ArrowheadStyle.OpenCircle, ArrowheadStyle.None)]
    [InlineData("openTriangle", ArrowheadStyle.OpenTriangle, ArrowheadStyle.None)]
    [InlineData("horizontalLine", ArrowheadStyle.HorizontalLine, ArrowheadStyle.None)]
    [InlineData("Straight", ArrowheadStyle.SolidTriangle, ArrowheadStyle.None)]
    [InlineData("Thin", ArrowheadStyle.SolidTriangle, ArrowheadStyle.None)]
    [InlineData("Double", ArrowheadStyle.SolidTriangle, ArrowheadStyle.SolidTriangle)]
    [InlineData(null, ArrowheadStyle.SolidTriangle, ArrowheadStyle.None)]
    public void ParseArrowhead_AcceptsSurveyAndLegacyNames(
        string? name, ArrowheadStyle end, ArrowheadStyle start)
    {
        var parsed = AnnotationStyle.ParseArrowhead(name, out var startHead);
        Assert.Equal(end, parsed);
        Assert.Equal(start, startHead);
    }

    [Fact]
    public void EnforceOneVisible_KeepsStrokeWhenBothTransparent()
    {
        var stroke = Color.FromArgb(0, 255, 0, 0);
        var fill = Color.FromArgb(0, 0, 0, 255);
        var (s, f) = AnnotationStyle.EnforceOneVisible(stroke, fill);
        Assert.Equal(255, s.A);
        Assert.Equal(0, f.A);
        Assert.Equal(255, s.R);
    }

    [Fact]
    public void EnforceOneVisible_LeavesPartialTransparencyAlone()
    {
        var stroke = Color.FromArgb(1, 255, 0, 0);
        var fill = Color.FromArgb(0, 0, 255, 0);
        var (s, f) = AnnotationStyle.EnforceOneVisible(stroke, fill);
        Assert.Equal(1, s.A);
        Assert.Equal(0, f.A);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 128)]
    [InlineData(1, 255)]
    [InlineData(-1, 0)]
    [InlineData(2, 255)]
    public void Compose_BakesOpacityIntoAlpha(double opacity, byte expectedAlpha)
    {
        var c = AnnotationStyle.Compose(Color.FromRgb(0xFF, 0x45, 0x3A), opacity);
        Assert.Equal(expectedAlpha, c.A);
        Assert.Equal(0xFF, c.R);
    }

    [Fact]
    public void Hsv_RoundTripsPrimaryColors()
    {
        foreach (var original in new[]
                 {
                     Color.FromRgb(255, 0, 0),
                     Color.FromRgb(0, 255, 0),
                     Color.FromRgb(0, 0, 255),
                     Color.FromRgb(255, 255, 255),
                     Color.FromRgb(0, 0, 0),
                     Color.FromRgb(255, 128, 0),
                 })
        {
            var (h, s, v) = AnnotationStyle.ToHsv(original);
            var back = AnnotationStyle.FromHsv(h, s, v);
            Assert.True(Math.Abs(original.R - back.R) <= 1, original.ToString());
            Assert.True(Math.Abs(original.G - back.G) <= 1, original.ToString());
            Assert.True(Math.Abs(original.B - back.B) <= 1, original.ToString());
        }
    }

    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("00FF00", 0, 255, 0)]
    [InlineData("#F00", 255, 0, 0)]
    [InlineData("#80FFFFFF", 255, 255, 255)]
    public void TryParseHex_AcceptsCommonForms(string text, byte r, byte g, byte b)
    {
        Assert.True(AnnotationStyle.TryParseHex(text, out var c));
        Assert.Equal(r, c.R);
        Assert.Equal(g, c.G);
        Assert.Equal(b, c.B);
    }

    [Fact]
    public void TryParseHex_RejectsGarbage()
    {
        Assert.False(AnnotationStyle.TryParseHex("not-a-color", out _));
        Assert.False(AnnotationStyle.TryParseHex("", out _));
        Assert.False(AnnotationStyle.TryParseHex(null, out _));
    }

    [Theory]
    [InlineData(0.4, 1)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(50.6, 50)]
    [InlineData(200, 50)]
    [InlineData(-3, 1)]
    public void ClampSize_StaysInSurveyRange(double input, int expected)
    {
        Assert.Equal(expected, AnnotationStyle.ClampSize(input));
    }
}
