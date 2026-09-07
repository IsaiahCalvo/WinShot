using System.Windows.Media;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class ColorMathTests
{
    [Fact]
    public void ContrastRatio_HitsTheKnownEnds()
    {
        Assert.Equal(21, ColorMath.ContrastRatio(Colors.White, Colors.Black), 2);
        Assert.Equal(1, ColorMath.ContrastRatio(Colors.White, Colors.White), 4);
    }

    /// <summary>
    /// The pill's whole contract: a label plate painted in ANY colour the picker offers
    /// still carries legible text, because the ink is chosen and not the user's raw pick.
    /// Black and #3A3A3C used to render near-black glyphs at 1.26:1 and 1.47:1.
    /// </summary>
    [Theory]
    [InlineData("#FF453A")] // the default annotation red
    [InlineData("#000000")]
    [InlineData("#3A3A3C")]
    [InlineData("#8E8E93")] // the closest thing in the grid to the worst possible plate
    [InlineData("#C7C7CC")]
    [InlineData("#FFFFFF")]
    [InlineData("#FFCC00")]
    [InlineData("#0A84FF")]
    public void TextOn_ClearsFourAndAHalfToOneOnEverySwatch(string hex)
    {
        var plate = (Color)ColorConverter.ConvertFromString(hex)!;

        Assert.True(ColorMath.ContrastRatio(ColorMath.TextOn(plate), plate) >= 4.5,
            $"{hex} -> {ColorMath.ContrastRatio(ColorMath.TextOn(plate), plate):0.00}:1");
    }

    [Fact]
    public void TextOn_NeverDropsBelowTheCrossoverPoint()
    {
        // The worst plate is the one where black and white read the same; nothing can beat
        // 4.58:1 there, and nothing may come out below it either.
        double worst = 21;
        for (int r = 0; r < 256; r += 5)
            for (int g = 0; g < 256; g += 5)
                for (int b = 0; b < 256; b += 5)
                {
                    var plate = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    worst = Math.Min(worst, ColorMath.ContrastRatio(ColorMath.TextOn(plate), plate));
                }

        Assert.True(worst >= 4.5, $"worst plate read {worst:0.00}:1");
    }
}
