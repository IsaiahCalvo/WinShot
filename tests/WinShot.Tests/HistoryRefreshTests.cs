using System.Drawing.Imaging;
using System.IO;
using WinShot.Core;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// A refresh keeps the HistoryItem behind any path that is still there, so a tile keeps its
/// decoded thumbnail and does not blink when one new capture lands. That is right for a folder
/// that gained a file and wrong for one whose bytes were rewritten under the same name — which
/// is what Rotate, and the quick-actions card's rotate/flip/resize, do. The stamp below is what
/// separates the two; the end-to-end version is ThemedWindowTests.HistoryRotateRefreshSmoke.
/// </summary>
public class HistoryRefreshTests
{
    [Fact]
    public void AnItemStillMatchesTheFileItWasBuiltFrom()
    {
        string dir = TempDir();
        try
        {
            string file = WritePng(dir, 200, 60);

            Assert.True(new HistoryItem(file).MatchesFileOnDisk());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void RewritingTheFileUnderTheSameNameStopsItMatching()
    {
        string dir = TempDir();
        try
        {
            string file = WritePng(dir, 200, 60);
            var item = new HistoryItem(file);

            // Exactly what OnRotate does: rotate, stage outside the folder, move over the top.
            using (var image = HistoryImageLoader.LoadDetachedBitmap(file))
            {
                image.RotateFlip(SD.RotateFlipType.Rotate90FlipNone);
                string staging = Path.Combine(Path.GetTempPath(), $"winshot-test-{Guid.NewGuid():N}.png");
                ImageSaver.Save(image, staging);
                File.Move(staging, file, overwrite: true);
            }

            Assert.False(item.MatchesFileOnDisk());
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AFileThatWentAwayDoesNotMatchEither()
    {
        string dir = TempDir();
        try
        {
            string file = WritePng(dir, 16, 16);
            var item = new HistoryItem(file);
            File.Delete(file);

            Assert.False(item.MatchesFileOnDisk());
        }
        finally { Cleanup(dir); }
    }

    /// <summary>A video whose frame could not be decoded is tried once, not on every reload,
    /// filter change and delete for the life of the window.</summary>
    [Fact]
    public void AVideoFrameIsOnlyAttemptedOnce()
    {
        var item = new HistoryItem(@"C:\sanitized-history\20260803-120000-000.mp4");

        Assert.False(item.VideoFrameTried);
        item.VideoFrameTried = true;
        Assert.True(item.VideoFrameTried);
        Assert.Null(item.Thumbnail);
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WritePng(string dir, int width, int height)
    {
        string path = Path.Combine(dir, "20260803-120000-000.png");
        using var bitmap = new SD.Bitmap(width, height);
        using (var graphics = SD.Graphics.FromImage(bitmap))
        {
            graphics.Clear(SD.Color.Teal);
            graphics.FillRectangle(SD.Brushes.White, 2, 2, Math.Max(1, width / 3), Math.Max(1, height / 3));
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
