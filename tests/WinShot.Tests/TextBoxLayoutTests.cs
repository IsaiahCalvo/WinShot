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
}
