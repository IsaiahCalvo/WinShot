using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace WinShot.Tests;

public class CaptureServiceTests
{
    [Fact]
    public void CloneBitmap_CopiesPixelsWithoutSharingSourceState()
    {
        using var source = new SD.Bitmap(2, 2, SD.Imaging.PixelFormat.Format32bppArgb);
        source.SetPixel(0, 0, SD.Color.FromArgb(255, 12, 34, 56));
        source.SetPixel(1, 0, SD.Color.FromArgb(255, 78, 90, 123));
        source.SetPixel(0, 1, SD.Color.FromArgb(255, 222, 111, 9));
        source.SetPixel(1, 1, SD.Color.FromArgb(255, 4, 5, 6));

        using var clone = CaptureService.CloneBitmap(source);
        source.SetPixel(0, 0, SD.Color.Black);

        Assert.Equal(SD.Color.FromArgb(255, 12, 34, 56).ToArgb(), clone.GetPixel(0, 0).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 78, 90, 123).ToArgb(), clone.GetPixel(1, 0).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 222, 111, 9).ToArgb(), clone.GetPixel(0, 1).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 4, 5, 6).ToArgb(), clone.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void Crop_CopiesExpectedRegionWithoutSharingSourceState()
    {
        using var source = new SD.Bitmap(4, 3, SD.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
                source.SetPixel(x, y, SD.Color.FromArgb(255, x * 40, y * 50, x + y));
        }

        using var crop = CaptureService.Crop(source, new SD.Rectangle(1, 1, 2, 2));
        source.SetPixel(1, 1, SD.Color.Black);

        Assert.Equal(2, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(SD.Color.FromArgb(255, 40, 50, 2).ToArgb(), crop.GetPixel(0, 0).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 80, 50, 3).ToArgb(), crop.GetPixel(1, 0).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 40, 100, 3).ToArgb(), crop.GetPixel(0, 1).ToArgb());
        Assert.Equal(SD.Color.FromArgb(255, 80, 100, 4).ToArgb(), crop.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public async Task ToBitmapSourceSnapshotAsync_WithMaxSize_DownsizesAndDetachesFromSource()
    {
        using var source = new SD.Bitmap(400, 200, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(source))
            g.Clear(SD.Color.FromArgb(255, 12, 34, 56));

        var snapshot = await CaptureService.ToBitmapSourceSnapshotAsync(source, 100, 100);

        Assert.True(snapshot.IsFrozen);
        Assert.Equal(100, snapshot.PixelWidth);
        Assert.Equal(50, snapshot.PixelHeight);
    }

    [Fact]
    public async Task ToBitmapSourceSnapshotAsync_DetachesBeforeQueuedConversion()
    {
        using var workerStarted = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        var enqueue = typeof(CaptureService).GetMethod(
            "RunBitmapSourceConversion",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Func<BitmapSource> blocker = () =>
        {
            workerStarted.Set();
            if (!releaseWorker.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Bitmap conversion worker was not released.");

            var pixel = BitmapSource.Create(
                1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32,
                palette: null,
                pixels: new byte[4],
                stride: 4);
            pixel.Freeze();
            return pixel;
        };

        var blockerTask = (Task<BitmapSource>)enqueue.Invoke(null, [blocker])!;
        Assert.True(workerStarted.Wait(TimeSpan.FromSeconds(5)), "Bitmap conversion worker did not start.");

        var source = new SD.Bitmap(320, 6000, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = SD.Graphics.FromImage(source))
            graphics.Clear(SD.Color.CornflowerBlue);

        Task<BitmapSource> snapshotTask;
        try
        {
            snapshotTask = CaptureService.ToBitmapSourceSnapshotAsync(source);
            source.Dispose();
        }
        finally
        {
            releaseWorker.Set();
        }

        await blockerTask;
        BitmapSource snapshot = await snapshotTask;

        Assert.True(snapshot.IsFrozen);
        Assert.Equal(320, snapshot.PixelWidth);
        Assert.Equal(6000, snapshot.PixelHeight);
    }
}
