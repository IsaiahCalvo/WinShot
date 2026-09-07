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

    /// <summary>
    /// The unit is chosen from the rounded value, not the raw one. Choosing first and
    /// rounding afterwards printed "1024 KB" for every size in the last half-kilobyte below
    /// a megabyte — the same shape of bug the duration above needed fixing for.
    /// </summary>
    [Theory]
    [InlineData(1024L * 1024 - 1)]
    [InlineData(1024L * 1024 - 400)]
    [InlineData(1024L * 1024)]
    public void ASizeJustUnderTheNextUnitRollsOverInsteadOfPrinting1024(long bytes)
        => Assert.Equal("1.0 MB", MediaCardStrip.FormatSize(bytes));

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

    [Fact]
    public void JustUnderAMinuteRollsOverInsteadOfPrintingSixtySeconds()
    {
        // The branch used to be picked on the unrounded value and the number rounded after,
        // so anything from 59.5 s up printed "60s".
        Assert.Equal("1:00", MediaCardStrip.FormatDuration(TimeSpan.FromSeconds(59.6)));
        Assert.Equal("59s", MediaCardStrip.FormatDuration(TimeSpan.FromSeconds(59.4)));
        Assert.Equal("1:00:00", MediaCardStrip.FormatDuration(TimeSpan.FromSeconds(3599.7)));
    }

    [Fact]
    public void TheTextRunStopsShortOfTheHoverButtonsThatOverlapTheStrip()
    {
        var card = new SD.Rectangle(0, 0, 192, 120);
        SD.Rectangle strip = MediaCardStrip.Bounds(card, 96);
        int pad = MediaCardStrip.Padding(96);
        // The bottom action row overlaps the strip's top edge at every card size.
        var left = new SD.Rectangle(6, strip.Top - 8, 22, 22);
        var right = new SD.Rectangle(card.Right - 28, strip.Top - 8, 22, 22);

        (int runLeft, int runRight) = MediaCardStrip.TextRun(strip, new[] { left, right }, pad);

        Assert.True(runLeft >= left.Right + pad);
        Assert.True(runRight <= right.Left - pad);
    }

    [Fact]
    public void AButtonThatMissesTheStripDoesNotNarrowTheTextRun()
    {
        var card = new SD.Rectangle(0, 0, 192, 120);
        SD.Rectangle strip = MediaCardStrip.Bounds(card, 96);
        int pad = MediaCardStrip.Padding(96);
        var high = new SD.Rectangle(6, card.Top + 6, 22, 22);

        (int runLeft, int runRight) = MediaCardStrip.TextRun(strip, new[] { high }, pad);

        Assert.Equal(strip.Left + pad, runLeft);
        Assert.Equal(strip.Right - pad, runRight);
    }

    [Fact]
    public void AStripSweptCleanByItsButtonsReportsNoRunRatherThanANegativeOne()
    {
        var card = new SD.Rectangle(0, 0, 192, 120);
        SD.Rectangle strip = MediaCardStrip.Bounds(card, 96);
        var wide = new SD.Rectangle(strip.Left, strip.Top, strip.Width, strip.Height);

        (int runLeft, int runRight) = MediaCardStrip.TextRun(strip, new[] { wide }, MediaCardStrip.Padding(96));

        Assert.True(runRight <= runLeft, "a run with no room must read as empty, not as a negative width");
    }
}
