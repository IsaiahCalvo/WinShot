using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The colour notations a picked colour can be written in, and the tolerant reader that takes
/// one back. Pure arithmetic, so it is checked against known values rather than eyeballed.
/// </summary>
public class ColorFormatterTests
{
    private static readonly SD.Color Red = SD.Color.FromArgb(255, 255, 59, 48);

    [Fact]
    public void HexFormats_AreWhatTheAppStoresAndWhatFieldsAccept()
    {
        Assert.Equal("#FF3B30", ColorFormatter.Format(Red, ColorFormat.Hex));
        Assert.Equal("FF3B30", ColorFormatter.Format(Red, ColorFormat.HexBare));
        Assert.Equal("#FFFF3B30", ColorFormatter.Format(Red, ColorFormat.HexArgb));
        Assert.Equal("rgb(255, 59, 48)", ColorFormatter.Format(Red, ColorFormat.Rgb));
    }

    [Theory]
    [InlineData(255, 255, 255, "hsl(0, 0%, 100%)")]
    [InlineData(0, 0, 0, "hsl(0, 0%, 0%)")]
    [InlineData(255, 0, 0, "hsl(0, 100%, 50%)")]
    [InlineData(0, 255, 0, "hsl(120, 100%, 50%)")]
    [InlineData(0, 0, 255, "hsl(240, 100%, 50%)")]
    public void HslMatchesTheStandardWheel(int r, int g, int b, string expected)
        => Assert.Equal(expected, ColorFormatter.Format(SD.Color.FromArgb(r, g, b), ColorFormat.Hsl));

    /// <summary>
    /// OKLCH against Ottosson's own reference values. Grey has no chroma and so no meaningful
    /// hue -- reporting whatever atan2 returns for numerical noise would be worse than 0.
    /// </summary>
    [Theory]
    [InlineData(255, 255, 255, "oklch(100% 0 0)")]
    [InlineData(0, 0, 0, "oklch(0% 0 0)")]
    [InlineData(255, 0, 0, "oklch(62.8% 0.258 29.23)")]
    [InlineData(0, 0, 255, "oklch(45.2% 0.313 264.05)")]
    public void OklchMatchesTheReferenceConversion(int r, int g, int b, string expected)
        => Assert.Equal(expected, ColorFormatter.Format(SD.Color.FromArgb(r, g, b), ColorFormat.Oklch));

    [Theory]
    [InlineData("#FF3B30")]
    [InlineData("FF3B30")]        // Shottr's bare hex, and what people paste
    [InlineData("  #ff3b30  ")]
    [InlineData("#FFFF3B30")]     // with alpha
    public void EveryHexShapeReadsBackAsTheSameColour(string text)
    {
        Assert.True(ColorFormatter.TryParse(text, out SD.Color parsed));
        Assert.Equal("#FF3B30", ColorFormatter.Format(parsed, ColorFormat.Hex));
    }

    /// <summary>Three-digit hex expands each nibble; #F30 is #FF3300, not #0F0300.</summary>
    [Fact]
    public void ShortHexExpandsEachNibble()
    {
        Assert.True(ColorFormatter.TryParse("#F30", out SD.Color parsed));
        Assert.Equal("#FF3300", ColorFormatter.Format(parsed, ColorFormat.Hex));
    }

    /// <summary>
    /// Every format the picker's "Copy as" menu offers produces something, and no two of them
    /// produce the same string -- a menu with two rows that copy identical text is a menu with
    /// a mistake in it.
    /// </summary>
    [Fact]
    public void EveryOfferedFormatIsDistinctAndNonEmpty()
    {
        string[] written = Enum.GetValues<ColorFormat>()
            .Select(format => ColorFormatter.Format(Red, format))
            .ToArray();

        Assert.All(written, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(written.Length, written.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#GGGGGG")]
    [InlineData("#FF3B3")]        // five digits is not a colour
    [InlineData("rgb(255,59,48)")] // written, not read: the reader takes hex only
    public void NonsenseIsRejected(string? text)
        => Assert.False(ColorFormatter.TryParse(text, out _));
}
