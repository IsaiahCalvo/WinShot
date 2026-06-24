using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class VideoTrimRangeTests
{
    [Fact]
    public void FromStart_ClampsStartBeforeEndGap()
    {
        var range = VideoTrimRange.FromStart(
            requestedStartSeconds: 9.95,
            currentEndSeconds: 10,
            durationSeconds: 10);

        Assert.Equal(9.9, range.StartSeconds, precision: 3);
        Assert.Equal(10, range.EndSeconds, precision: 3);
    }

    [Fact]
    public void FromEnd_ClampsEndAfterStartGap()
    {
        var range = VideoTrimRange.FromEnd(
            currentStartSeconds: 5,
            requestedEndSeconds: 5.02,
            durationSeconds: 10);

        Assert.Equal(5, range.StartSeconds, precision: 3);
        Assert.Equal(5.1, range.EndSeconds, precision: 3);
    }

    [Fact]
    public void Normalize_ClampsValuesToDurationAndMinimumRange()
    {
        var range = VideoTrimRange.Normalize(
            startSeconds: -5,
            endSeconds: 99,
            durationSeconds: 8);

        Assert.Equal(0, range.StartSeconds, precision: 3);
        Assert.Equal(8, range.EndSeconds, precision: 3);
        Assert.True(range.IsExportable);
    }

    [Fact]
    public void Normalize_HandlesInvalidDuration()
    {
        var range = VideoTrimRange.Normalize(
            startSeconds: double.NaN,
            endSeconds: double.PositiveInfinity,
            durationSeconds: double.NaN);

        Assert.Equal(0, range.StartSeconds, precision: 3);
        Assert.Equal(VideoTrimRange.MinimumDurationSeconds, range.EndSeconds, precision: 3);
        Assert.True(range.IsExportable);
    }

    [Theory]
    [InlineData(0, 0.05, false)]
    [InlineData(0, 0.1, true)]
    [InlineData(2, 1, false)]
    public void IsExportable_RequiresMinimumPositiveDuration(double start, double end, bool expected)
    {
        Assert.Equal(expected, VideoTrimRange.CanExport(start, end));
    }

    [Fact]
    public void TrimFromEnd_UsesRemainingDurationWithoutGoingNegative()
    {
        var range = VideoTrimRange.Normalize(2, 7.5, 10);

        Assert.Equal(2.5, range.TrimFromEndSeconds(10), precision: 3);
        Assert.Equal(0, range.TrimFromEndSeconds(6), precision: 3);
    }
}
