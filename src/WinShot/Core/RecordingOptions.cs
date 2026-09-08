namespace WinShot.Core;

public static class RecordingOptions
{
    public const int MinCountdownSeconds = 0;
    public const int MaxCountdownSeconds = 60;
    public const int MinWebcamSizePercent = 10;
    public const int MaxWebcamSizePercent = 45;
    public const int DefaultWebcamSizePercent = 22;
    public const string WebcamOff = "off";
    public const string WebcamFullscreen = "fullscreen";

    /// <summary>Where the webcam overlay goes the first time it is switched on.</summary>
    public const string WebcamDefaultCorner = "bottom-right";

    /// <summary>H.264 quality (1-100). 70 is the "Medium" the options modal always defaulted to.</summary>
    public const int DefaultVideoQuality = 70;

    /// <summary>
    /// A stored quality of 0 or less is not "as bad as possible" — it is a settings.json that
    /// never carried the key, or a migration that zero-initialised it. Clamping it to 1 records
    /// the worst H.264 the encoder can make; treat it as unset and use the default instead.
    /// </summary>
    public static int ClampVideoQuality(int quality) =>
        quality <= 0 ? DefaultVideoQuality : Math.Clamp(quality, 1, 100);

    private static readonly HashSet<string> WebcamPositions =
    [
        WebcamOff,
        "top-left",
        "top-right",
        "bottom-left",
        "bottom-right",
        WebcamFullscreen,
    ];

    public static int ClampCountdownSeconds(int seconds) =>
        Math.Clamp(seconds, MinCountdownSeconds, MaxCountdownSeconds);

    public static int ClampWebcamSizePercent(int percent) =>
        Math.Clamp(percent, MinWebcamSizePercent, MaxWebcamSizePercent);

    public static string NormalizeWebcamPosition(string? position) =>
        position is not null && WebcamPositions.Contains(position) ? position : WebcamOff;

    /// <summary>
    /// WebcamOverlayPosition is both the on/off flag and the corner, so writing "off" over it
    /// erases the corner the user picked. This is the value kept beside it: the last position
    /// that was not "off", falling back to the default corner.
    /// </summary>
    public static string RememberedWebcamCorner(string? position, string? remembered)
    {
        string next = NormalizeWebcamPosition(position);
        if (!string.Equals(next, WebcamOff, StringComparison.Ordinal))
            return next;

        string kept = NormalizeWebcamPosition(remembered);
        return string.Equals(kept, WebcamOff, StringComparison.Ordinal) ? WebcamDefaultCorner : kept;
    }

    public const string KeystrokeDefaultPosition = "bottom-center";

    // The webcam's corner vocabulary plus the two centred slots a keystroke pill actually
    // wants; there is no "off" here, because the pill's own toggle is what turns it off.
    private static readonly HashSet<string> KeystrokePositions =
    [
        "top-left",
        "top-center",
        "top-right",
        "bottom-left",
        KeystrokeDefaultPosition,
        "bottom-right",
    ];

    public static string NormalizeKeystrokePosition(string? position) =>
        position is not null && KeystrokePositions.Contains(position) ? position : KeystrokeDefaultPosition;
}
