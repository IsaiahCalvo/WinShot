using System.Drawing.Imaging;
using System.IO;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>What a recorded GIF is made of, as the trim editor needs to see it.</summary>
public readonly record struct GifInfo(int Width, int Height, int FrameCount, int DelayCentiseconds)
{
    public double FrameSeconds => Math.Max(0.01, DelayCentiseconds / 100.0);

    public double DurationSeconds => FrameCount * FrameSeconds;

    /// <summary>Frames per second, rounded to something the encoder will accept.</summary>
    public int Fps => Math.Clamp((int)Math.Round(100.0 / Math.Max(1, DelayCentiseconds)), 1, 50);

    public bool IsUsable => Width > 0 && Height > 0 && FrameCount > 0;
}

/// <summary>
/// The frame arithmetic behind trimming a GIF. Pure, because "which frames survive" is the
/// whole edit and off-by-one there silently drops the last frame of every take.
/// </summary>
public static class GifTrim
{
    /// <summary>
    /// The inclusive frame range covering [<paramref name="startSeconds"/>,
    /// <paramref name="endSeconds"/>]. Always at least one frame, always inside the clip.
    /// </summary>
    public static (int First, int Last) FrameRange(
        int frameCount,
        double frameSeconds,
        double startSeconds,
        double endSeconds)
    {
        if (frameCount <= 0)
            return (0, -1);

        double step = frameSeconds > 0 && double.IsFinite(frameSeconds) ? frameSeconds : 0.01;
        int first = Clamp((int)Math.Floor(CleanSeconds(startSeconds) / step), frameCount);
        // The frame containing the out point is kept: an end exactly on a frame boundary is
        // the boundary of the frame BEFORE it, not the start of one the user trimmed away.
        int last = Clamp((int)Math.Ceiling(CleanSeconds(endSeconds) / step) - 1, frameCount);
        if (last < first)
            last = first;
        return (first, last);
    }

    public static int FrameCount((int First, int Last) range) => Math.Max(0, range.Last - range.First + 1);

    private static int Clamp(int index, int frameCount) => Math.Clamp(index, 0, frameCount - 1);

    private static double CleanSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}

/// <summary>
/// Reads and re-writes recorded GIFs with what is already in the box: System.Drawing decodes
/// the frames (the format <see cref="GifEncoder"/> writes), and <see cref="GifEncoder"/> writes
/// them back out. No new dependency, and no attempt to make MediaElement swallow a GIF.
/// </summary>
public static class GifEditing
{
    // PropertyTagFrameDelay: one 32-bit delay per frame, in centiseconds.
    private const int FrameDelayPropertyId = 0x5100;

    public static GifInfo Read(string path)
    {
        using SD.Image image = SD.Image.FromFile(path);
        return Describe(image);
    }

    /// <summary>Decodes one frame as an owned bitmap. Callers dispose it.</summary>
    public static SD.Bitmap? ReadFrame(string path, int frameIndex)
    {
        try
        {
            using SD.Image image = SD.Image.FromFile(path);
            var dimension = new FrameDimension(image.FrameDimensionsList[0]);
            int count = image.GetFrameCount(dimension);
            if (count <= 0)
                return null;
            image.SelectActiveFrame(dimension, Math.Clamp(frameIndex, 0, count - 1));
            return new SD.Bitmap(image);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to decode frame {frameIndex} of {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes the frames in [first, last] to <paramref name="destinationPath"/> through the
    /// app's own encoder. Returns the number of frames written; zero means nothing was
    /// written and the destination was cleaned up.
    /// </summary>
    public static int Trim(string sourcePath, string destinationPath, int firstFrame, int lastFrame)
    {
        int written = 0;
        try
        {
            using SD.Image image = SD.Image.FromFile(sourcePath);
            GifInfo info = Describe(image);
            if (!info.IsUsable)
                return 0;

            var dimension = new FrameDimension(image.FrameDimensionsList[0]);
            int first = Math.Clamp(firstFrame, 0, info.FrameCount - 1);
            int last = Math.Clamp(lastFrame, first, info.FrameCount - 1);

            using (var output = File.Create(destinationPath))
            using (var encoder = new GifEncoder(output, info.Width, info.Height, info.Fps))
            {
                for (int i = first; i <= last; i++)
                {
                    image.SelectActiveFrame(dimension, i);
                    using var frame = new SD.Bitmap(image);
                    encoder.AddFrame(frame);
                    written++;
                }
                encoder.Finish();
            }

            return written;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to trim {sourcePath}", ex);
            TryDelete(destinationPath);
            return 0;
        }
    }

    private static GifInfo Describe(SD.Image image)
    {
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        int frames = image.GetFrameCount(dimension);
        return new GifInfo(image.Width, image.Height, frames, ReadDelayCentiseconds(image));
    }

    /// <summary>
    /// The per-frame delay, taken from the first frame. WinShot writes one uniform delay for
    /// the whole file, so reading frame 0 describes every frame; a GIF from elsewhere with
    /// varying delays is re-timed to its first, which is the honest limit of a re-encode
    /// through a fixed-fps encoder.
    /// </summary>
    private static int ReadDelayCentiseconds(SD.Image image)
    {
        try
        {
            PropertyItem? item = image.PropertyItems.FirstOrDefault(p => p.Id == FrameDelayPropertyId);
            if (item?.Value is { Length: >= 4 } value)
            {
                int delay = BitConverter.ToInt32(value, 0);
                if (delay > 0)
                    return delay;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read GIF frame delay; assuming 10 fps", ex);
        }

        return 10; // GIF default when nothing says otherwise
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to remove the partial trimmed GIF {path}", ex);
        }
    }
}
