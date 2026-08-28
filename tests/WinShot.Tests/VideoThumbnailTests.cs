using System.IO;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class VideoThumbnailTests
{
    [Fact]
    public async Task CreateAsync_UnreadableFile_ReturnsPlaceholder()
    {
        using SD.Bitmap bmp = await VideoThumbnail.CreateAsync(
            Path.Combine(Path.GetTempPath(), $"winshot-missing-{Guid.NewGuid():N}.mp4"));
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    [Fact]
    public async Task CreateAsync_Gif_ReturnsFirstFrame()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winshot-thumb-{Guid.NewGuid():N}.gif");
        try
        {
            using (var source = new SD.Bitmap(40, 30))
            {
                using (var g = SD.Graphics.FromImage(source))
                    g.Clear(SD.Color.Red);
                source.Save(path, SD.Imaging.ImageFormat.Gif);
            }

            using SD.Bitmap bmp = await VideoThumbnail.CreateAsync(path);
            Assert.Equal(40, bmp.Width);
            Assert.Equal(30, bmp.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
