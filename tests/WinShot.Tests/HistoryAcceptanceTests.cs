using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using WinShot.Core;
using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistoryAcceptanceTests
{
    [Fact]
    public void Delete_ReturnsSuccessOnlyAfterFileIsGone()
    {
        string dir = CreateTempDir();
        try
        {
            string path = WriteFile(dir, "20260731-120000-000.png");
            HistoryDeleteResult result = CreateService(dir).Delete(path);

            Assert.True(result.Succeeded);
            Assert.Null(result.ErrorMessage);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Delete_ReturnsFailureAndKeepsFileWhenDiskDeletionFails()
    {
        string dir = CreateTempDir();
        try
        {
            string path = WriteFile(dir, "20260731-120000-000.png");
            using var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            HistoryDeleteResult result = CreateService(dir).Delete(path);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void ClipboardBitmap_IsDetachedFromDisposedFileStream()
    {
        string dir = CreateTempDir();
        string path = Path.Combine(dir, "20260731-120000-000.png");
        try
        {
            using (var source = new Bitmap(3, 2))
            {
                source.SetPixel(2, 1, Color.CornflowerBlue);
                source.Save(path, ImageFormat.Png);
            }

            using Bitmap detached = HistoryImageLoader.LoadDetachedBitmap(path);
            File.Delete(path);

            Assert.Equal(Color.CornflowerBlue.ToArgb(), detached.GetPixel(2, 1).ToArgb());
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Theory]
    [InlineData("capture.png", true)]
    [InlineData("capture.jpg", true)]
    [InlineData("capture.gif", false)]
    [InlineData("capture.mp4", false)]
    public void PinAvailability_IsLimitedToLoadableStillImages(string fileName, bool canPin)
    {
        var item = new HistoryItem(fileName);

        Assert.Equal(canPin, item.CanPin);
        Assert.Equal(canPin ? Visibility.Visible : Visibility.Collapsed, item.PinVisibility);
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 1.0, 510, 240, 900, 600)]
    [InlineData(1920, 0, 2560, 1440, 1.5, 2525, 270, 1350, 900)]
    [InlineData(-2560, 0, 2560, 1440, 2.0, -2180, 120, 1800, 1200)]
    public void WindowPlacement_CentersInCursorMonitorWorkArea(
        int x, int y, int width, int height, double scale,
        int expectedLeft, int expectedTop, int expectedWidth, int expectedHeight)
    {
        Rectangle placement = HistoryWindowPlacement.CenterInWorkArea(
            new System.Drawing.Rectangle(x, y, width, height),
            new System.Windows.Size(900, 600),
            scale);

        Assert.Equal(expectedLeft, placement.Left);
        Assert.Equal(expectedTop, placement.Top);
        Assert.Equal(expectedWidth, placement.Width);
        Assert.Equal(expectedHeight, placement.Height);
    }

    [Fact]
    public void HistoryXaml_DeclaresAccessibleKeyboardAndTypeSpecificActionContract()
    {
        string xaml = File.ReadAllText(FindRepositoryFile("src", "WinShot", "History", "HistoryWindow.xaml"));

        Assert.Contains("WindowStartupLocation=\"Manual\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Capture history\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpText=\"Use arrow keys", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocused\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocusWithin\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding PinVisibility}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectionToolbar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Scrolling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRestore\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCopySelected\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnShareSelected\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDeleteSelected\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LoadMoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnLoadMore\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Shows the next 200 local history items", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20260803-120000-000-scroll.png", true)]
    [InlineData("20260803-120000-000.png", false)]
    [InlineData("20260803-120000-000-scroll.mp4", false)]
    public void ScrollingClassification_UsesExplicitHistoryTag(string fileName, bool expected)
    {
        var item = new HistoryItem(fileName);

        Assert.Equal(expected, item.IsScrollingCapture);
        Assert.Equal(expected ? Visibility.Visible : Visibility.Collapsed, item.ScrollingVisibility);
    }

    private static HistoryService CreateService(string dir)
    {
        var settings = new SettingsService();
        settings.Current.HistoryLimit = 200;
        return new HistoryService(settings, () => dir);
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

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = segments.Aggregate(directory.FullName, Path.Combine);
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {Path.Combine(segments)}");
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
