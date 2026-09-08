using System.IO;
using System.Drawing;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class HistoryServiceTests
{
    /// <summary>
    /// Recordings share the history folder with screenshots, so anything that wants "the last
    /// screenshot" -- Pin last, the card's restore -- has to skip them. Pin last used to take
    /// the newest file of any kind and report "It is not an image WinShot can read" after
    /// every recording.
    /// </summary>
    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("clip.MOV", true)]
    [InlineData("clip.webm", true)]
    [InlineData("shot.png", false)]
    [InlineData("shot.gif", false)]   // a GIF is still an image the pin can show
    [InlineData("shot", false)]
    [InlineData(null, false)]
    public void RecordingsAreToldApartFromScreenshots(string? name, bool expected)
        => Assert.Equal(expected, HistoryService.IsVideo(name));

    [Fact]
    public void Add_ScrollingCapture_WritesDurableClassificationTag()
    {
        string historyDir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryLimit = 200;
            var history = new HistoryService(settings, () => historyDir);
            using var bitmap = new Bitmap(2, 2);

            string path = history.Add(bitmap, HistoryCaptureKind.Scrolling)!;

            Assert.EndsWith("-scroll.png", path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path));
            Assert.True(new WinShot.History.HistoryItem(path).IsScrollingCapture);
        }
        finally
        {
            DeleteTempDir(historyDir);
        }
    }
    /// <summary>
    /// Count pruning has to order by capture time, not by file name. Rename drops the
    /// "yyyyMMdd-HHmmss-fff" prefix on purpose, so a renamed capture whose name sorts below "2"
    /// used to fall to the bottom of the list and be the first thing the next capture deleted -
    /// and the user renames precisely the captures they mean to keep.
    /// </summary>
    [Fact]
    public void Prune_KeepsARenamedCaptureThatIsStillTheNewest()
    {
        string historyDir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryLimit = 3;
            var history = new HistoryService(settings, () => historyDir);

            var paths = new List<string>();
            for (int index = 0; index < 3; index++)
            {
                using var bitmap = new Bitmap(2, 2);
                paths.Add(history.Add(bitmap)!);
                System.Threading.Thread.Sleep(5);
            }

            // The user renames the newest capture - the one they just took.
            WinShot.History.HistoryRename.Result renamed =
                WinShot.History.HistoryRename.Resolve(paths[2], "1099 tax form");
            Assert.Null(renamed.Error);
            File.Move(paths[2], renamed.Path!);

            // One more capture: the limit is 3, so exactly one file goes.
            using (var bitmap = new Bitmap(2, 2))
                history.Add(bitmap);

            Assert.True(
                File.Exists(renamed.Path!),
                "the renamed capture was pruned though it was the newest. Remaining: "
                    + string.Join(", ", Directory.GetFiles(historyDir).Select(Path.GetFileName)));
            Assert.False(File.Exists(paths[0]));
        }
        finally
        {
            DeleteTempDir(historyDir);
        }
    }

    [Fact]
    public void HistoryDisabled_WritesNothingToDisk()
    {
        string historyDir = CreateTempDir();
        string sourceDir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryEnabled = false;
            var history = new HistoryService(settings, () => historyDir);
            using var bitmap = new Bitmap(2, 2);
            string source = WriteFile(sourceDir, "source.mp4");

            Assert.Null(history.Add(bitmap));
            Assert.Null(history.AddFile(source));
            Assert.Empty(history.GetItems());
            Assert.Empty(Directory.GetFiles(historyDir));
        }
        finally
        {
            DeleteTempDir(historyDir);
            DeleteTempDir(sourceDir);
        }
    }

    [Fact]
    public void AddFile_PrunesExpiredFilesWhenRetentionIsConfigured()
    {
        string historyDir = CreateTempDir();
        string sourceDir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryLimit = 200;
            settings.Current.HistoryRetentionDays = 1;
            var history = new HistoryService(settings, () => historyDir);
            string oldHistoryFile = WriteFile(historyDir, "20260101-000000-000.png");
            File.SetLastWriteTime(oldHistoryFile, DateTime.Now.AddDays(-2));
            string source = WriteFile(sourceDir, "source.png");

            string added = history.AddFile(source)!;

            Assert.False(File.Exists(oldHistoryFile));
            Assert.True(File.Exists(added));
            Assert.Single(history.GetItems());
        }
        finally
        {
            DeleteTempDir(historyDir);
            DeleteTempDir(sourceDir);
        }
    }

    [Fact]
    public void AddFile_KeepsExpiredFilesWhenRetentionIsDisabled()
    {
        string historyDir = CreateTempDir();
        string sourceDir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryLimit = 200;
            settings.Current.HistoryRetentionDays = 0;
            var history = new HistoryService(settings, () => historyDir);
            string oldHistoryFile = WriteFile(historyDir, "20260101-000000-000.png");
            File.SetLastWriteTime(oldHistoryFile, DateTime.Now.AddDays(-2));
            string source = WriteFile(sourceDir, "source.png");

            string added = history.AddFile(source)!;

            Assert.True(File.Exists(oldHistoryFile));
            Assert.True(File.Exists(added));
            Assert.Equal(2, history.GetItems().Count);
        }
        finally
        {
            DeleteTempDir(historyDir);
            DeleteTempDir(sourceDir);
        }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
