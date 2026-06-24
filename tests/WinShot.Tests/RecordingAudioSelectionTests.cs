using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

public class RecordingAudioSelectionTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void FromChoices_MapsRecorderAudioFlags(
        bool microphone,
        bool systemAudio,
        bool audioEnabled,
        bool inputEnabled,
        bool outputEnabled)
    {
        var selection = RecordingAudioSelection.FromChoices(microphone, systemAudio);

        Assert.Equal(audioEnabled, selection.IsAudioEnabled);
        Assert.Equal(inputEnabled, selection.IsInputDeviceEnabled);
        Assert.Equal(outputEnabled, selection.IsOutputDeviceEnabled);
    }
}
