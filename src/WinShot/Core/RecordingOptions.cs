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
}
