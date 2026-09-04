using WinShot.Ocr;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Which OCR results earn an "Open link" button. This used to live inline in App with no
/// coverage at all, and it is the one OCR decision that navigates somewhere on a click.
/// </summary>
public class OcrLinkTests
{
    [Theory]
    [InlineData("https://example.com/thing")]
    [InlineData("http://example.com")]
    [InlineData("  https://example.com/a?b=c  ")]
    public void ABareWebAddress_IsOffered(string text)
        => Assert.NotNull(OcrLink.Detect(text, detectLinks: true));

    [Theory]
    [InlineData("Visit https://example.com for details")]  // text that contains a link is text
    [InlineData("https://example.com https://other.com")]  // two links: which one would it open?
    [InlineData("mailto:someone@example.com")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    [InlineData("just some recognized text")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_IsNot(string? text)
        => Assert.Null(OcrLink.Detect(text, detectLinks: true));

    /// <summary>
    /// The user's standing answer wins over the content. Settings and the toast's own
    /// "Don't detect links" both write the same flag, so both have to reach this.
    /// </summary>
    [Fact]
    public void WithDetectionOff_NothingIsOffered()
        => Assert.Null(OcrLink.Detect("https://example.com", detectLinks: false));
}
