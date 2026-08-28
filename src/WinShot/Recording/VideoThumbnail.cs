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
    public static async Task<SD.Bitmap> CreateAsync(string filePath)
    {
        try
        {
            if (filePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                using var image = SD.Image.FromFile(filePath);
                return new SD.Bitmap(image);
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
            return new SD.Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not render a thumbnail for {filePath}", ex);
            return Placeholder();
        }
    }

    private static SD.Bitmap Placeholder()
    {
        var bmp = new SD.Bitmap(320, 180);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(35, 35, 38));
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(120, 255, 255, 255));
        g.FillPolygon(brush, new[] { new SD.Point(140, 60), new SD.Point(140, 120), new SD.Point(190, 90) });
        return bmp;
    }
}
