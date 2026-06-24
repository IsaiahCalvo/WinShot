using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class HistoryServiceTests
{
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

            string added = history.AddFile(source);

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

            string added = history.AddFile(source);

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
