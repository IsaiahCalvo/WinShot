using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class MediaCardStripTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(3, "3s")]
    [InlineData(53, "53s")]
    [InlineData(60, "1:00")]
    [InlineData(83, "1:23")]
    [InlineData(599, "9:59")]
    [InlineData(3723, "1:02:03")]
    public void DurationReadsTheWayAVideoPlayerWritesIt(int seconds, string expected)
        => Assert.Equal(expected, MediaCardStrip.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ANegativeDurationIsTreatedAsZeroRatherThanPrintingAMinusSign()
        => Assert.Equal("0s", MediaCardStrip.FormatDuration(TimeSpan.FromSeconds(-4)));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(812L, "812 B")]
    [InlineData(86016L, "84 KB")]
    [InlineData(7130316L, "6.8 MB")]
    [InlineData(48234496L, "46 MB")]
    [InlineData(1288490189L, "1.2 GB")]
    public void SizeCarriesADecimalOnlyWhereItMeansSomething(long bytes, string expected)
        => Assert.Equal(expected, MediaCardStrip.FormatSize(bytes));

    [Fact]
    public void TheStripSitsFlushWithTheBottomOfTheCardAndScalesWithIt()
    {
        var card = new SD.Rectangle(0, 0, 192, 120);

        SD.Rectangle at96 = MediaCardStrip.Bounds(card, 96);
        SD.Rectangle at144 = MediaCardStrip.Bounds(new SD.Rectangle(0, 0, 288, 180), 144);

        Assert.Equal(card.Bottom, at96.Bottom);
        Assert.Equal(card.Left, at96.Left);
        Assert.Equal(card.Width, at96.Width);
        Assert.Equal(17, at96.Height);
        // A card and a half means a strip and a half; a raw constant would read half-height.
        Assert.Equal(26, at144.Height);
        Assert.True(MediaCardStrip.Padding(144) > MediaCardStrip.Padding(96));
    }

    [Fact]
    public void TheGradientStartsAboveTheStripSoItFadesInsteadOfBanding()
    {
        var card = new SD.Rectangle(0, 0, 192, 120);

        SD.Rectangle strip = MediaCardStrip.Bounds(card, 96);
        SD.Rectangle band = MediaCardStrip.GradientBounds(card, 96);

        Assert.Equal(strip.Bottom, band.Bottom);
        Assert.True(band.Top < strip.Top);
        Assert.True(band.Top >= card.Top);
    }

    [Fact]
    public void TheStripNeverOverflowsATinyCard()
    {
        var card = new SD.Rectangle(0, 0, 40, 10);

        SD.Rectangle band = MediaCardStrip.GradientBounds(card, 192);

        Assert.True(band.Top >= card.Top);
        Assert.Equal(card.Bottom, band.Bottom);
    }
}
