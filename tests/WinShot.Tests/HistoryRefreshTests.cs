using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using WinShot.Core;
using WinShot.History;
using Xunit;
using SD = System.Drawing;
using SWMI = System.Windows.Media.Imaging;

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

    /// <summary>A still image whose decode failed is tried once too, and says so on the tile:
    /// the shimmer is a promise that a picture is coming, and it must not outlive the attempt.</summary>
    [Fact]
    public void AThumbnailThatFailedToDecodeStopsShimmeringAndSaysSo()
    {
        var item = new HistoryItem(@"C:\sanitized-history\20260803-120000-000.png");

        Assert.False(item.ThumbnailTried);
        Assert.Equal(Visibility.Visible, item.MissingThumbnailVisibility);
        Assert.Equal(Visibility.Collapsed, item.UnreadableVisibility);

        item.ThumbnailTried = true;

        // The placeholder stays (there is still no picture) but the sweep stops and the tile
        // swaps the "loading" glyph for the warning one.
        Assert.Equal(Visibility.Visible, item.MissingThumbnailVisibility);
        Assert.Equal(Visibility.Collapsed, item.ShimmerVisibility);
        Assert.Equal(Visibility.Visible, item.UnreadableVisibility);
        Assert.Equal("\uE7BA", item.PlaceholderGlyph);
    }

    /// <summary>And a decode that worked shows no failure mark, tried or not.</summary>
    [Fact]
    public void AThumbnailThatDecodedShowsNoPlaceholderAtAll()
    {
        string dir = TempDir();
        try
        {
            var item = new HistoryItem(WritePng(dir, 32, 32))
            {
                ThumbnailTried = true,
                Thumbnail = new SWMI.BitmapImage(new Uri(WritePng(dir, 32, 32))),
            };

            Assert.Equal(Visibility.Collapsed, item.MissingThumbnailVisibility);
            Assert.Equal(Visibility.Collapsed, item.ShimmerVisibility);
            Assert.Equal(Visibility.Collapsed, item.UnreadableVisibility);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The reload diff: a file whose bytes are unchanged keeps its item (and its thumbnail),
    /// one that was rewritten under the same name is rebuilt, and one that went away is not
    /// live. This is the loop that used to run inside Dispatcher.InvokeAsync and freeze the
    /// window for the length of a page — it is pure and static now so it can run off-thread.
    /// </summary>
    [Fact]
    public void ADiffKeepsUnchangedItemsAndRebuildsRewrittenOnes()
    {
        string dir = TempDir();
        try
        {
            string kept = WritePng(dir, 40, 40);
            string rewritten = Path.Combine(dir, "20260803-120001-000.png");
            File.Copy(kept, rewritten);
            string gone = Path.Combine(dir, "20260803-120002-000.png");
            File.Copy(kept, gone);

            var previous = new List<HistoryItem>
            {
                new(kept) { Thumbnail = new SWMI.BitmapImage(new Uri(kept)) },
                new(rewritten) { Thumbnail = new SWMI.BitmapImage(new Uri(kept)) },
                new(gone) { Thumbnail = new SWMI.BitmapImage(new Uri(kept)) },
            };

            using (var image = HistoryImageLoader.LoadDetachedBitmap(rewritten))
            {
                image.RotateFlip(SD.RotateFlipType.Rotate90FlipNone);
                string staging = Path.Combine(Path.GetTempPath(), $"winshot-test-{Guid.NewGuid():N}.png");
                ImageSaver.Save(image, staging);
                File.Move(staging, rewritten, overwrite: true);
            }

            File.Delete(gone);

            (List<HistoryItem> items, HashSet<HistoryItem> live) =
                HistoryWindow.DiffItems(new[] { kept, rewritten }, previous);

            Assert.Equal(2, items.Count);
            Assert.Same(previous[0], items[0]);
            Assert.NotSame(previous[1], items[1]);
            Assert.Null(items[1].Thumbnail);
            Assert.Contains(previous[0], live);
            Assert.DoesNotContain(previous[1], live);
            Assert.DoesNotContain(previous[2], live);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The hole the "keep an item that has no thumbnail yet" clause left: a decode that already
    /// FAILED also has no thumbnail, so the failed item was reused on every later diff no matter
    /// what happened to the bytes, and both load passes skip an item whose Tried flag is set. A
    /// capture copied into the folder is decoded mid-write, fails, and the tile then said "can't
    /// read this picture" over a finished, perfectly readable file until History was reopened.
    /// </summary>
    [Fact]
    public void ADiffRebuildsAnItemWhoseDecodeFailedOverAFileThatChanged()
    {
        string dir = TempDir();
        try
        {
            string file = WritePng(dir, 40, 40);
            var failed = new HistoryItem(file) { ThumbnailTried = true };
            Assert.Null(failed.Thumbnail);

            // Same bytes: the tile really is unreadable, so re-decoding it every reload would be
            // the shimmer-forever bug the flag exists to stop.
            (List<HistoryItem> unchanged, _) = HistoryWindow.DiffItems(new[] { file }, new[] { failed });
            Assert.Same(failed, unchanged[0]);

            // Rewritten under the same name — the copy finished, or a rotate landed. A fresh item
            // is what lets the decode run again.
            Thread.Sleep(20);
            using (var bitmap = new SD.Bitmap(64, 64))
            {
                using (var graphics = SD.Graphics.FromImage(bitmap)) graphics.Clear(SD.Color.Coral);
                bitmap.Save(file, ImageFormat.Png);
            }

            (List<HistoryItem> rebuilt, HashSet<HistoryItem> live) =
                HistoryWindow.DiffItems(new[] { file }, new[] { failed });
            Assert.NotSame(failed, rebuilt[0]);
            Assert.False(rebuilt[0].ThumbnailTried);
            Assert.DoesNotContain(failed, live);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>The video half of the same flag: a clip whose frame would not decode is rebuilt
    /// once the file changes, rather than keeping the "MP4" card for good.</summary>
    [Fact]
    public void ADiffRebuildsAVideoWhoseFrameFailedOverAFileThatChanged()
    {
        string dir = TempDir();
        try
        {
            string file = Path.Combine(dir, "20260803-120000-000.mp4");
            File.WriteAllBytes(file, new byte[64]);
            var failed = new HistoryItem(file) { VideoFrameTried = true };

            (List<HistoryItem> unchanged, _) = HistoryWindow.DiffItems(new[] { file }, new[] { failed });
            Assert.Same(failed, unchanged[0]);

            Thread.Sleep(20);
            File.WriteAllBytes(file, new byte[256]);
            (List<HistoryItem> rebuilt, _) = HistoryWindow.DiffItems(new[] { file }, new[] { failed });
            Assert.NotSame(failed, rebuilt[0]);
            Assert.False(rebuilt[0].VideoFrameTried);
        }
        finally { Cleanup(dir); }
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
