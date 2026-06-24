using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

public readonly record struct RecordingWebcamOverlayLayout(
    string Position,
    int SizePercent,
    int Width,
    int Height,
    int OffsetPx,
    bool IsFullscreen)
{
    public const int DefaultOffsetPx = 16;

    public static bool TryCreate(
        SD.Rectangle recordingRect,
        string? position,
        int sizePercent,
        out RecordingWebcamOverlayLayout layout)
    {
        string normalizedPosition = RecordingOptions.NormalizeWebcamPosition(position);
        if (normalizedPosition == RecordingOptions.WebcamOff ||
            recordingRect.Width < 2 ||
            recordingRect.Height < 2)
        {
            layout = default;
            return false;
        }

        if (normalizedPosition == RecordingOptions.WebcamFullscreen)
        {
            layout = new RecordingWebcamOverlayLayout(
                normalizedPosition,
                100,
                recordingRect.Width,
                recordingRect.Height,
                0,
                true);
            return true;
        }

        int normalizedPercent = RecordingOptions.ClampWebcamSizePercent(sizePercent);
        int requestedWidth = Math.Max(2, (int)Math.Round(recordingRect.Width * (normalizedPercent / 100.0)));
        int maxWidth = Math.Max(2, recordingRect.Width - (DefaultOffsetPx * 2));
        int maxWidthFromHeight = Math.Max(2, (int)Math.Floor((recordingRect.Height - (DefaultOffsetPx * 2)) * (4.0 / 3.0)));
        int width = Math.Min(requestedWidth, Math.Min(maxWidth, maxWidthFromHeight));
        int height = Math.Max(2, (int)Math.Round(width * 0.75));

        layout = new RecordingWebcamOverlayLayout(
            normalizedPosition,
            normalizedPercent,
            width,
            height,
            DefaultOffsetPx,
            false);
        return true;
    }
}
