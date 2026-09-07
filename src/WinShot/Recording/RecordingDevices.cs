using ScreenRecorderLib;
using WinShot.Core;

namespace WinShot.Recording;

/// <summary>One microphone or camera the recorder can be pointed at.</summary>
public readonly record struct RecordingDevice(string DeviceName, string Label);

/// <summary>
/// The capture hardware, enumerated fresh on every ask — a webcam plugged in after launch has
/// to appear without a restart. Shared by the record-display modal and the Settings pane so
/// both offer the same list and store the same DeviceName.
///
/// Best effort by design: a machine that refuses to enumerate gets an empty list, which reads
/// as "system default" everywhere it is used.
/// </summary>
public static class RecordingDevices
{
    public static IReadOnlyList<RecordingDevice> Microphones() =>
        Enumerate(
            () => Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices)
                ?.Select(d => new RecordingDevice(d.DeviceName ?? "", d.FriendlyName ?? d.DeviceName ?? "")),
            "microphone");

    public static IReadOnlyList<RecordingDevice> Cameras() =>
        Enumerate(
            () => Recorder.GetSystemVideoCaptureDevices()
                ?.Select(d => new RecordingDevice(d.DeviceName ?? "", d.FriendlyName ?? d.DeviceName ?? "")),
            "webcam");

    private static IReadOnlyList<RecordingDevice> Enumerate(
        Func<IEnumerable<RecordingDevice>?> query, string what)
    {
        try
        {
            return query()?.Where(d => d.DeviceName.Length > 0).ToArray() ?? [];
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to enumerate {what} devices", ex);
            return [];
        }
    }
}
