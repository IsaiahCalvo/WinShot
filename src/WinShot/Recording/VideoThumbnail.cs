using System.IO;
using Windows.Media.Editing;
using Windows.Storage;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// First-frame thumbnail for a finished recording: MP4 via MediaComposition (same
/// recipe as the video editor filmstrip), GIF via System.Drawing. Never throws —
/// falls back to a plain dark play-button card so the overlay always has a face.
/// </summary>
public static class VideoThumbnail
{
    /// <summary>
    /// The poster frame and how long the recording runs. The card prints the duration in
    /// its bottom strip, and the file is already open here — asking again later would mean
    /// a second MediaComposition load.
    /// </summary>
    public static async Task<(SD.Bitmap Thumbnail, TimeSpan? Duration)> CreateWithDurationAsync(string filePath)
    {
        try
        {
            if (filePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                using var image = SD.Image.FromFile(filePath);
                return (new SD.Bitmap(image), GifDuration(image));
            }

            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath));
            var clip = await MediaClip.CreateFromFileAsync(file);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var props = clip.GetVideoEncodingProperties();
            int width = (int)Math.Max(1, props.Width);
            int height = (int)Math.Max(1, props.Height);
            if (width > 640)
            {
                height = Math.Max(1, height * 640 / width);
                width = 640;
            }

            var stream = await composition.GetThumbnailAsync(
                TimeSpan.Zero, width, height, VideoFramePrecision.NearestKeyFrame);
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;
            using var decoded = SD.Image.FromStream(ms);
            return (new SD.Bitmap(decoded), clip.OriginalDuration);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not render a thumbnail for {filePath}", ex);
            return (Placeholder(), null);
        }
    }

    /// <summary>
    /// A GIF has no duration field; it is the sum of its per-frame delays, which GDI+ hands
    /// back as one array of hundredths of a second in property 0x5100.
    /// </summary>
    private static TimeSpan? GifDuration(SD.Image image)
    {
        try
        {
            const int frameDelay = 0x5100;
            if (Array.IndexOf(image.PropertyIdList, frameDelay) < 0)
                return null;

            byte[] value = image.GetPropertyItem(frameDelay)?.Value ?? Array.Empty<byte>();
            long hundredths = 0;
            for (int i = 0; i + 4 <= value.Length; i += 4)
                hundredths += BitConverter.ToInt32(value, i);
            return hundredths > 0 ? TimeSpan.FromMilliseconds(hundredths * 10) : null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read a GIF's frame delays", ex);
            return null;
        }
    }

    private static SD.Bitmap Placeholder()
    {
        var bmp = new SD.Bitmap(320, 180);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(ThemePalette.WindowBg);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(120, 255, 255, 255));
        g.FillPolygon(brush, new[] { new SD.Point(140, 60), new SD.Point(140, 120), new SD.Point(190, 90) });
        return bmp;
    }
}
