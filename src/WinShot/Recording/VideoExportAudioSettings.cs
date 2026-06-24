namespace WinShot.Recording;

public readonly record struct VideoExportAudioSettings(
    double ClipVolume,
    uint? OutputChannelCount)
{
    public static VideoExportAudioSettings FromControls(
        bool mute,
        double volume,
        bool convertToMono)
    {
        double normalizedVolume = mute ? 0 : Math.Clamp(volume, 0, 1);
        return new VideoExportAudioSettings(
            normalizedVolume,
            convertToMono ? 1u : null);
    }
}
