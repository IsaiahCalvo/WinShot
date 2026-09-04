using System.IO;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Trimming a recorded GIF: the frame arithmetic (off-by-one here silently drops the last
/// frame of every take) and a real round trip through the app's own encoder and decoder.
/// </summary>
public class GifEditingTests
{
    [Theory]
    [InlineData(0, 5, 0, 49)]        // whole clip
    [InlineData(1, 2, 10, 19)]       // one second in the middle at 10 fps
    [InlineData(0, 0.1, 0, 0)]       // the first frame alone
    [InlineData(4.9, 5, 49, 49)]     // the last frame alone
    public void FrameRange_CoversTheSecondsAsked(double start, double end, int first, int last)
    {
        var range = GifTrim.FrameRange(frameCount: 50, frameSeconds: 0.1, start, end);

        Assert.Equal((first, last), range);
    }

    [Fact]
    public void FrameRange_NeverProducesAnEmptyOrInvertedClip()
    {
        Assert.Equal((0, 0), GifTrim.FrameRange(50, 0.1, 0, 0));
        Assert.Equal((49, 49), GifTrim.FrameRange(50, 0.1, 100, 100));
        Assert.Equal(1, GifTrim.FrameCount(GifTrim.FrameRange(50, 0.1, 3, 2)));
    }

    [Fact]
    public void FrameRange_SurvivesNonsenseInput()
    {
        Assert.Equal((0, -1), GifTrim.FrameRange(0, 0.1, 0, 5));
        var range = GifTrim.FrameRange(10, 0, double.NaN, double.PositiveInfinity);
        Assert.InRange(range.First, 0, 9);
        Assert.InRange(range.Last, range.First, 9);
    }

    [Fact]
    public void FrameCount_IsInclusive()
    {
        Assert.Equal(1, GifTrim.FrameCount((4, 4)));
        Assert.Equal(10, GifTrim.FrameCount((0, 9)));
        Assert.Equal(0, GifTrim.FrameCount((0, -1)));
    }

    // ---- a real round trip ---------------------------------------------------------

    private static string WriteGif(string path, int frames, int width = 24, int height = 16, int fps = 10)
    {
        using (var output = File.Create(path))
        using (var encoder = new GifEncoder(output, width, height, fps))
        {
            for (int i = 0; i < frames; i++)
            {
                using var frame = new SD.Bitmap(width, height);
                using (SD.Graphics g = SD.Graphics.FromImage(frame))
                    g.Clear(SD.Color.FromArgb(255, (i * 25) % 256, 40, 200));
                encoder.AddFrame(frame);
            }
            encoder.Finish();
        }
        return path;
    }

    [Fact]
    public void Read_DescribesWhatTheEncoderWrote()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = WriteGif(Path.Combine(directory, "take.gif"), frames: 12);

            GifInfo info = GifEditing.Read(source);

            Assert.True(info.IsUsable);
            Assert.Equal(24, info.Width);
            Assert.Equal(16, info.Height);
            Assert.Equal(12, info.FrameCount);
            Assert.Equal(10, info.Fps);
            Assert.Equal(1.2, info.DurationSeconds, 3);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Trim_KeepsExactlyTheFramesAskedFor()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = WriteGif(Path.Combine(directory, "take.gif"), frames: 12);
            string destination = Path.Combine(directory, "take (edited).gif");

            int written = GifEditing.Trim(source, destination, firstFrame: 3, lastFrame: 7);

            Assert.Equal(5, written);
            GifInfo trimmed = GifEditing.Read(destination);
            Assert.Equal(5, trimmed.FrameCount);
            Assert.Equal(24, trimmed.Width);
            Assert.Equal(16, trimmed.Height);
            // The source is never touched.
            Assert.Equal(12, GifEditing.Read(source).FrameCount);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Trim_ClampsAnOutOfRangeRequestRatherThanFailing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = WriteGif(Path.Combine(directory, "take.gif"), frames: 6);
            string destination = Path.Combine(directory, "clamped.gif");

            int written = GifEditing.Trim(source, destination, firstFrame: -5, lastFrame: 500);

            Assert.Equal(6, written);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Trim_LeavesNoPartialFileWhenTheSourceIsNotAGif()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "not-a.gif");
            File.WriteAllText(source, "this is not a GIF");
            string destination = Path.Combine(directory, "out.gif");

            int written = GifEditing.Trim(source, destination, 0, 1);

            Assert.Equal(0, written);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReadFrame_ReturnsNullForAFileThatIsNotAGif()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winshot-not-a-gif-{Guid.NewGuid():N}.gif");
        File.WriteAllText(path, "nope");
        try
        {
            Assert.Null(GifEditing.ReadFrame(path, 0));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
