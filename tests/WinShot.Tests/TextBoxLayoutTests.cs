using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class TextBoxLayoutTests
{
    [Fact]
    public void TextPadding_MatchesSurvey()
    {
        Assert.Equal(6, AnnotationFactory.TextPadding);
        Assert.Equal(160, AnnotationFactory.DefaultTextBoxWidth);
        Assert.Equal(120, CalloutLayout.DefaultBoxWidth);
        Assert.Equal(32, CalloutLayout.DefaultBoxHeight);
        Assert.Equal(40, CalloutLayout.CreateKneeLength);
    }

    [Theory]
    [InlineData(null, "top")]
    [InlineData("top", "top")]
    [InlineData("middle", "middle")]
    [InlineData("bottom", "bottom")]
    [InlineData("nope", "top")]
    public void VerticalAlign_RoundTrip(string? stored, string expected)
    {
        Assert.Equal(expected, AnnotationFactory.VerticalAlignName(AnnotationFactory.ParseVerticalAlign(stored)));
    }

    /// <summary>
    /// Survey's new-text mount height: <c>fontSize * 1.16 * 1.13 + 2 * TEXT_PADDING</c>
    /// (TextEditOverlay.jsx:165) — font-independent, and 32.97 rather than 36 at 16px.
    /// </summary>
    [Fact]
    public void SingleLineBoxHeight_MatchesSurveysFontDerivedMountHeight()
    {
        Assert.Equal(1.16 * 1.13, AnnotationFactory.TextLineHeightFactor, 10);
        Assert.Equal(0.35, AnnotationFactory.TextDescenderRatio);
        Assert.Equal(32.9728, AnnotationFactory.SingleLineBoxHeight(16), 4);
        // The family no longer participates: Survey's factor is the same for every font.
        Assert.Equal(
            AnnotationFactory.SingleLineBoxHeight(16, "Helvetica"),
            AnnotationFactory.SingleLineBoxHeight(16, "Courier New"));
    }

    /// <summary>Survey's FONT_SIZE_PRESETS and its 6..200 programmatic clamp.</summary>
    [Fact]
    public void FontSizes_MatchSurvey()
    {
        Assert.Equal(
            new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72 },
            AnnotationStyle.TextSizePresets);
        Assert.Equal(6, AnnotationStyle.ClampTextSize(1));
        Assert.Equal(200, AnnotationStyle.ClampTextSize(1000));
        Assert.Equal(100, AnnotationStyle.ClampTextSize(100));
    }

    /// <summary>Survey's family list has no system UI font (AppShell.jsx:1833).</summary>
    [Fact]
    public void FontFamilies_MatchSurvey() =>
        Assert.Equal(
            new[] { "Arial", "Helvetica", "Times New Roman", "Courier New", "Georgia", "Verdana" },
            AnnotationStyle.FontFamilies);
}
