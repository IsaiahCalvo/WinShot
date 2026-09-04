using System.Linq;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The export size promise and the resolution labels behind it. Both are pure, and both are
/// numbers a person reads and then trusts, so they are pinned rather than eyeballed.
/// </summary>
public class VideoExportEstimateTests
{
    [Fact]
    public void Bytes_IsBitrateTimesDurationOverEight()
    {
        long bytes = VideoExportEstimate.Bytes(videoBitrate: 8_000_000, audioBitrate: 0, seconds: 10);

        Assert.Equal(10_000_000, bytes);
    }

    [Fact]
    public void Bytes_CountsTheAudioTrackToo()
    {
        long muted = VideoExportEstimate.Bytes(4_000_000, 0, 30);
        long withAudio = VideoExportEstimate.Bytes(4_000_000, VideoExportEstimate.AudioBitrate, 30);

        Assert.True(withAudio > muted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Bytes_IsZeroForANonsenseDuration(double seconds) =>
        Assert.Equal(0, VideoExportEstimate.Bytes(8_000_000, 0, seconds));

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(400, "1 KB")]
    [InlineData(500_000, "488 KB")]
    [InlineData(8_073_216, "7.7 MB")]
    [InlineData(52_428_800, "50 MB")]
    public void FormatBytes_ReadsLikeAFileSize(long bytes, string expected) =>
        Assert.Equal(expected, VideoExportEstimate.FormatBytes(bytes));

    [Fact]
    public void FormatBytes_NeverSaysZeroForAFileThatExists() =>
        Assert.NotEqual("0 KB", VideoExportEstimate.FormatBytes(1));

    [Fact]
    public void ComparedToSource_ReportsTheSaving() =>
        Assert.Equal("61% of the original", VideoExportEstimate.ComparedToSource(610, 1000));

    /// <summary>An output that grew is not a saving, and claiming "140% of the original" as
    /// good news would be worse than saying nothing.</summary>
    [Theory]
    [InlineData(1400, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(0, 1000)]
    [InlineData(500, 0)]
    [InlineData(500, -1)]
    public void ComparedToSource_SaysNothingWhenThereIsNothingToSay(long output, long source) =>
        Assert.Null(VideoExportEstimate.ComparedToSource(output, source));

    // ---- resolution presets --------------------------------------------------------

    [Fact]
    public void Presets_LabelEveryChoiceWithItsRealPixels()
    {
        var presets = VideoResolutionPresets.For(1920, 1080);

        Assert.Equal("1920 × 1080 (Original)", presets[0].Label);
        Assert.Equal("1440 × 810 (75%)", presets[1].Label);
        Assert.Equal("960 × 540 (50%)", presets[2].Label);
    }

    [Fact]
    public void Presets_ResolveNamedTiersAgainstTheRealAspect()
    {
        // A 9:16 phone recording: 720p means 720 tall, not 720 wide.
        var presets = VideoResolutionPresets.For(1080, 1920);

        VideoResolutionPreset tier = presets.Single(p => p.Label.Contains("720p"));
        Assert.Equal(720u, tier.Height);
        Assert.Equal(404u, tier.Width);
    }

    [Fact]
    public void Presets_DoNotOfferToUpscale()
    {
        var presets = VideoResolutionPresets.For(1280, 720);

        Assert.DoesNotContain(presets, p => p.Label.Contains("1080p"));
        Assert.DoesNotContain(presets, p => p.Label.Contains("720p")); // that IS the original
        Assert.All(presets, p => Assert.True(p.Width <= 1280 && p.Height <= 720));
    }

    [Fact]
    public void Presets_AreAlwaysEvenBecauseH264DemandsIt()
    {
        foreach (var presets in new[]
        {
            VideoResolutionPresets.For(1919, 1079),
            VideoResolutionPresets.For(1002, 563),
            VideoResolutionPresets.For(3, 3),
        })
        {
            Assert.All(presets, p =>
            {
                Assert.Equal(0u, p.Width % 2);
                Assert.Equal(0u, p.Height % 2);
                Assert.True(p.Width >= 2 && p.Height >= 2);
            });
        }
    }

    [Fact]
    public void Presets_FallBackToTheSameDefaultTheExportPathUses()
    {
        var presets = VideoResolutionPresets.For(0, 0);

        Assert.Equal(1920u, presets[0].Width);
        Assert.Equal(1080u, presets[0].Height);
    }

    [Fact]
    public void ExportSettings_FromSizeMatchesFromControlsForTheSameOutput()
    {
        var byIndex = VideoExportVideoSettings.FromControls(1920, 1080, 30, 1, 0, 0);
        var bySize = VideoExportVideoSettings.FromSize(1440, 810, 30, 0, 0);

        Assert.Equal(byIndex.Width, bySize.Width);
        Assert.Equal(byIndex.Height, bySize.Height);
        Assert.Equal(byIndex.Bitrate, bySize.Bitrate);
        Assert.Equal(byIndex.FrameRate, bySize.FrameRate);
    }
}
