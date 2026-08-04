using WinShot.Core;
using Xunit;
using SD = System.Drawing;

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
    public async Task ToBitmapSourceTilesBorrowedAsync_CopiesTallSourceIntoFrozenTiles()
    {
        var source = new SD.Bitmap(320, 6000, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = SD.Graphics.FromImage(source))
            graphics.Clear(SD.Color.CornflowerBlue);

        var tiles = await CaptureService.ToBitmapSourceTilesBorrowedAsync(source, tileHeight: 2048);
        source.Dispose();

        Assert.Equal(3, tiles.Count);
        Assert.Equal(6000, tiles.Sum(tile => tile.PixelHeight));
        Assert.All(tiles, tile => Assert.True(tile.IsFrozen));
        var pixel = new byte[4];
        tiles[0].CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal([237, 149, 100, 255], pixel);
    }

    [Fact]
    public async Task ToBitmapSourceTilesBorrowedAsync_UsesBoundedTilesForNon32BitSource()
    {
        using var source = new SD.Bitmap(240, 3000, SD.Imaging.PixelFormat.Format24bppRgb);
        using (var graphics = SD.Graphics.FromImage(source))
            graphics.Clear(SD.Color.Orchid);

        var tiles = await CaptureService.ToBitmapSourceTilesBorrowedAsync(source, tileHeight: 2048);

        Assert.Equal(2, tiles.Count);
        Assert.Equal(3000, tiles.Sum(tile => tile.PixelHeight));
        Assert.All(tiles, tile => Assert.True(tile.IsFrozen));
    }

    [Fact]
    public void EstimateEditorCaptureMemory_HasNoFullResolutionCloneFanOut()
    {
        const int width = 4096;
        const int height = 32768; // exactly 512 MiB at 32 bpp
        EditorCaptureMemoryBudget budget = CaptureService.EstimateEditorCaptureMemory(
            width, height, tileHeight: 2048);

        Assert.Equal(512L * 1024 * 1024, budget.SourceBytes);
        Assert.Equal(0, budget.FullResolutionCloneCount);
        Assert.True(budget.PeakPixelBytesWithoutAutoCopy < 1100L * 1024 * 1024);
        Assert.Equal(1536L * 1024 * 1024, budget.PeakPixelBytesWithAutoCopy);
    }
}
