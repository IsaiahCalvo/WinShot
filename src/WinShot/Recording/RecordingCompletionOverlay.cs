using WinShot.Core;
using WinShot.Overlay;
using WinShot.Pin;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Shows a finished recording in the same Quick Access thumbnail card screenshots
/// get: pin/copy/save/drag act on the file, Edit opens the video editor when one
/// applies (MP4). Replaces the old text-only recording toast.
/// </summary>
public static class RecordingCompletionOverlay
{
    public static async void Show(string filePath, SettingsService settings, Action? onEdit)
    {
        try
        {
            (SD.Bitmap thumbnail, TimeSpan? duration) = await VideoThumbnail.CreateWithDurationAsync(filePath);
            var overlay = FastQuickActionsWindow.CreateForMediaFile(
                filePath,
                thumbnail,
                settings,
                canEdit: onEdit is not null,
                duration: duration);
            if (onEdit is not null)
            {
                overlay.EditRequested += o =>
                {
                    onEdit();
                    o.Close();
                };
            }
            overlay.PinRequested += o =>
            {
                var pin = new FastPinWindow(o.CloneImage(), settings);
                // Same trade as App.PinFromOverlay: the card only skips its exit flight when
                // the pin is going to grow out of it.
                bool grows = pin.GrowFrom(o.CardScreenBounds);
                PerfLog.TrackFirstShown(pin, "pin window");
                pin.Show();
                if (grows)
                    o.CloseWithoutMotion();
                else
                    o.Close();
            };
            PerfLog.TrackFirstShown(overlay, "recording overlay");
            overlay.Show();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to show the recording overlay for {filePath}", ex);
        }
    }
}
