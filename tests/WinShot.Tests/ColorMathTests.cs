using System.Drawing;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class ColorMathTests
{
    [Fact]
    public void WcagAgreesWithTheTwoRatiosEverybodyKnowsByHeart()
    {
        Assert.Equal(21.0, ColorMath.ContrastRatio(Color.Black, Color.White), 2);
        Assert.Equal(1.0, ColorMath.ContrastRatio(Color.White, Color.White), 3);
    }

    [Fact]
    public void WcagDoesNotCareWhichColourYouCallTheText() =>
        Assert.Equal(
            ColorMath.ContrastRatio(Color.FromArgb(0x0A, 0x84, 0xFF), Color.FromArgb(0x1C, 0x1C, 0x1E)),
            ColorMath.ContrastRatio(Color.FromArgb(0x1C, 0x1C, 0x1E), Color.FromArgb(0x0A, 0x84, 0xFF)),
            6);

    [Fact]
    public void ApcaScoresBlackOnWhiteAtTheReferenceValue() =>
        // APCA-W3 0.1.9's own reference pair: Lc 106.04.
        Assert.Equal(106.04, ColorMath.ApcaLc(Color.Black, Color.White), 1);

    [Fact]
    public void ApcaIsPolarityAwareWhereWcagIsNot()
    {
        double darkOnLight = ColorMath.ApcaLc(Color.Black, Color.White);
        double lightOnDark = ColorMath.ApcaLc(Color.White, Color.Black);

        Assert.True(darkOnLight > 0);
        Assert.True(lightOnDark < 0);
        Assert.NotEqual(darkOnLight, Math.Abs(lightOnDark), 1);
    }

    [Fact]
    public void ApcaReportsZeroRatherThanNoiseWhenTwoColoursAreTheSame() =>
        Assert.Equal(0, ColorMath.ApcaLc(Color.FromArgb(0x40, 0x40, 0x40), Color.FromArgb(0x40, 0x40, 0x40)), 6);

    [Fact]
    public void TheLabelSaysWhichScaleItIsSoTheTwoAreNeverConfused()
    {
        Assert.Equal("WCAG 21.0 : 1", ColorMath.ContrastLabel(Color.Black, Color.White, apca: false));
        Assert.StartsWith("APCA Lc 106", ColorMath.ContrastLabel(Color.Black, Color.White, apca: true));
    }

    [Fact]
    public void TheDominantPairIsTheGroundAndTheThingWithTheMostContrastOnIt()
    {
        // A white card with black text on it: 90% white, 10% black, plus a speck of red.
        Color Sample(int x, int y)
        {
            if (x == 0 && y == 0) return Color.Red;
            return y < 90 ? Color.White : Color.Black;
        }

        var (background, foreground) = ColorMath.DominantPair(Sample, new Rectangle(0, 0, 100, 100));

        Assert.Equal(Color.White.ToArgb(), background.ToArgb());
        Assert.Equal(Color.Black.ToArgb(), foreground.ToArgb());
    }

    [Fact]
    public void AFlatRegionReportsItselfTwiceRatherThanInventingAContrast()
    {
        var (background, foreground) = ColorMath.DominantPair(
            (_, _) => Color.FromArgb(0x20, 0x20, 0x20), new Rectangle(0, 0, 40, 40));

        Assert.Equal(background.ToArgb(), foreground.ToArgb());
        Assert.Equal(1.0, ColorMath.ContrastRatio(background, foreground), 3);
    }

    [Fact]
    public void AnEmptyRegionAnswersInsteadOfThrowing()
    {
        var (background, foreground) = ColorMath.DominantPair((_, _) => Color.Red, Rectangle.Empty);

        Assert.NotEqual(background.ToArgb(), foreground.ToArgb());
    }

    [Fact]
    public void SamplingIsBoundedSoA4KRegionCostsTheSameAsASmallOne()
    {
        int reads = 0;
        Color Sample(int x, int y) { reads++; return Color.Gray; }

        ColorMath.DominantPair(Sample, new Rectangle(0, 0, 3840, 2160), maxSamplesPerAxis: 48);

        Assert.True(reads <= 48 * 48 + 96, $"sampled {reads} pixels");
    }
}
