using System.IO;
using System.Linq;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class TempFileJanitorTests
{
    [Fact]
    public void DeleteOldFiles_RemovesOnlyExpiredFiles()
    {
        string dir = CreateTempDir();
        try
        {
            var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
            string oldFile = WriteFile(dir, "old.png", now.AddDays(-2));
            string freshFile = WriteFile(dir, "fresh.png", now.AddHours(-1));
            string childDir = Path.Combine(dir, "child");
            Directory.CreateDirectory(childDir);
            string childFile = WriteFile(childDir, "old-child.png", now.AddDays(-2));

            int deleted = TempFileJanitor.DeleteOldFiles(dir, now, TimeSpan.FromDays(1));

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(freshFile));
            Assert.True(Directory.Exists(childDir));
            Assert.True(File.Exists(childFile));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void DeleteOldFiles_StopsAtDeleteLimit()
    {
        string dir = CreateTempDir();
        try
        {
            var now = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
            WriteFile(dir, "old-1.png", now.AddDays(-4));
            WriteFile(dir, "old-2.png", now.AddDays(-3));
            WriteFile(dir, "old-3.png", now.AddDays(-2));

            int deleted = TempFileJanitor.DeleteOldFiles(dir, now, TimeSpan.FromDays(1), maxFilesToDelete: 2);

            Assert.Equal(2, deleted);
            Assert.Single(Directory.EnumerateFiles(dir));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteFile(string dir, string name, DateTimeOffset lastWriteUtc)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
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
