using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

public class TextBoxLayoutTests
{
    [Fact]
    public void TextPadding_MatchesSurvey()
    {
        Assert.Equal(6, AnnotationFactory.TextPadding);
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
