using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class VideoExportAudioSettingsTests
{
    [Theory]
    [InlineData(false, 0.75, 0.75)]
    [InlineData(false, -1, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(true, 0.75, 0)]
    public void FromControls_ClampsExportVolume(bool mute, double volume, double expected)
    {
        var settings = VideoExportAudioSettings.FromControls(mute, volume, convertToMono: false);

        Assert.Equal(expected, settings.ClipVolume, precision: 3);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 1u)]
    public void FromControls_RequestsMonoOutputOnlyWhenEnabled(bool convertToMono, uint? expected)
    {
        var settings = VideoExportAudioSettings.FromControls(mute: false, volume: 1, convertToMono);

        Assert.Equal(expected, settings.OutputChannelCount);
    }
}
