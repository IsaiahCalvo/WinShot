using System.IO;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class FileNamerTests
{
    [Fact]
    public void NextUniquePath_AppendsSuffixWhenGeneratedNameAlreadyExists()
    {
        string dir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.FileNameTemplate = "Repeated capture";
            File.WriteAllText(Path.Combine(dir, "Repeated capture.png"), "");

            string path = FileNamer.NextUniquePath(settings, dir, "png");

            Assert.Equal(Path.Combine(dir, "Repeated capture 2.png"), path);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void NextUniquePath_ConsumesNumberTokenOnlyOnceWhenAddingCollisionSuffix()
    {
        string dir = CreateTempDir();
        try
        {
            var settings = new SettingsService();
            settings.Current.FileNameTemplate = "Capture {n}";
            settings.Current.NextCounter = 7;
            File.WriteAllText(Path.Combine(dir, "Capture 7.webp"), "");

            string path = FileNamer.NextUniquePath(settings, dir, ".webp");

            Assert.Equal(Path.Combine(dir, "Capture 7 2.webp"), path);
            Assert.Equal(8, settings.Current.NextCounter);
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
