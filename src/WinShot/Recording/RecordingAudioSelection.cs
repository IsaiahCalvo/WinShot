namespace WinShot.Recording;

public readonly record struct RecordingAudioSelection(
    bool IsAudioEnabled,
    bool IsInputDeviceEnabled,
    bool IsOutputDeviceEnabled)
{
    public static RecordingAudioSelection FromChoices(bool microphone, bool systemAudio) =>
        new(
            IsAudioEnabled: microphone || systemAudio,
            IsInputDeviceEnabled: microphone,
            IsOutputDeviceEnabled: systemAudio);
}
