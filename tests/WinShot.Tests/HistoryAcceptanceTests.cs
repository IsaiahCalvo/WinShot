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
        Assert.Contains("x:Name=\"CategoryRow\" Grid.Row=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectionCommandRow\" Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
        int selectionRowStart = xaml.IndexOf("x:Name=\"SelectionCommandRow\"", StringComparison.Ordinal);
        int categoryRowStart = xaml.IndexOf("x:Name=\"CategoryRow\"", StringComparison.Ordinal);
        int sortControlsStart = xaml.IndexOf("x:Name=\"SortControls\"", StringComparison.Ordinal);
        Assert.InRange(sortControlsStart, selectionRowStart, categoryRowStart - 1);
        Assert.Contains("Content=\"Scrolling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRestore\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCopySelected\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnShareSelected\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnDeleteSelected\"", xaml, StringComparison.Ordinal);
        // Everything the quick-actions card can do to a capture is reachable here too.
        Assert.Contains("Click=\"OnRotate\" Tag=\"rotate-left\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRotate\" Tag=\"rotate-right\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRename\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPrint\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenWith\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NewCaptureButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RetryLoadButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LoadMoreButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnLoadMore\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Shows the next 200 local history items", xaml, StringComparison.Ordinal);

        // History used to fork the theme with its own accent and neutral button styles at four
        // different paddings. Every command button here is one of the two shared pills now; the
        // only local Button style left is the one drawn on top of a thumbnail.
        Assert.DoesNotContain("x:Key=\"ToolbarButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"AccentToolbarButton\"", xaml, StringComparison.Ordinal);
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(xaml, "<Style x:Key=\"[A-Za-z]+\" TargetType=\"Button\""));
        Assert.Contains("Style=\"{StaticResource PillButtonPrimary}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PillButtonSecondary}\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20260803-120000-000-scroll.png", true)]
    [InlineData("20260803-120000-000.png", false)]
    [InlineData("20260803-120000-000-scroll.mp4", false)]
    // The source-app tag comes after the classification tag, so it must not hide it.
    [InlineData("20260803-120000-000-scroll@chrome.png", true)]
    [InlineData("20260803-120000-000@chrome.png", false)]
    public void ScrollingClassification_UsesExplicitHistoryTag(string fileName, bool expected)
    {
        var item = new HistoryItem(fileName);

        Assert.Equal(expected, item.IsScrollingCapture);
        Assert.Equal(expected ? Visibility.Visible : Visibility.Collapsed, item.ScrollingVisibility);
    }

    [Theory]
    [InlineData("20260803-120000-000@chrome.png", "chrome")]
    [InlineData("20260803-120000-000-scroll@Code.png", "Code")]
    [InlineData("20260803-120000-000.png", "")]
    // Nothing after the '@' is not a tag, and neither is a name that is only a tag.
    [InlineData("20260803-120000-000@.png", "")]
    [InlineData("@chrome.png", "")]
    public void SourceAppTag_IsReadBackFromTheFileName(string fileName, string expected)
    {
        var item = new HistoryItem(fileName);

        Assert.Equal(expected, item.SourceApp);
        // The icon has not been resolved yet, so no badge is shown either way.
        Assert.Equal(Visibility.Collapsed, item.SourceAppVisibility);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(0.4, "1s")]
    [InlineData(17, "17s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1:00")]
    [InlineData(125, "2:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3725, "1:02:05")]
    public void DurationLabel_ReadsAsAClipLength(double seconds, string expected) =>
        Assert.Equal(expected, HistoryItem.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void VideoTile_ShowsTheFallbackCardUntilTheFrameArrives()
    {
        var item = new HistoryItem("20260803-120000-000.mp4");

        Assert.Equal(Visibility.Visible, item.MediaFallbackVisibility);
        Assert.Equal(Visibility.Collapsed, item.ThumbnailVisibility);
        Assert.Equal(Visibility.Visible, item.MediaBarVisibility);

        item.Duration = TimeSpan.FromSeconds(17);
        item.Thumbnail = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        Assert.Equal(Visibility.Collapsed, item.MediaFallbackVisibility);
        Assert.Equal(Visibility.Visible, item.ThumbnailVisibility);
        Assert.StartsWith("17s", item.MediaBarText, StringComparison.Ordinal);
    }

    [Fact]
    public void Shimmer_RunsOnlyWhileTheThumbnailIsMissing()
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => true;
        try
        {
            var item = new HistoryItem("20260803-120000-000.png");
            Assert.Equal(Visibility.Visible, item.ShimmerVisibility);

            item.Thumbnail = new System.Windows.Media.Imaging.WriteableBitmap(
                2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            Assert.Equal(Visibility.Collapsed, item.ShimmerVisibility);

            // Animation off system-wide leaves the plain placeholder, not a frozen gradient.
            Motion.AnimationsEnabled = () => false;
            Assert.Equal(
                Visibility.Collapsed,
                new HistoryItem("20260803-120001-000.png").ShimmerVisibility);
        }
        finally
        {
            Motion.AnimationsEnabled = previous;
        }
    }

    /// <summary>
    /// The cap is on the LONGEST edge, not the width. History used to reimplement DragPreview's
    /// sizing and cap the width alone, so a scrolling capture — 1200x9000, and a first-class
    /// WinShot capture type that lands right here — dragged as a topmost 200x1500 strip trailing
    /// the cursor down the whole screen. Both callers spend DragPreview's one number now.
    /// </summary>
    [Theory]
    [InlineData(180, 120, 180, 120)]     // already inside the cap: handed back untouched
    [InlineData(640, 360, 180, 101)]     // a video frame decodes far larger and is scaled down
    [InlineData(1200, 9000, 24, 180)]    // a scrolling capture: the HEIGHT is what binds
    [InlineData(200, 2000, 18, 180)]     // and a portrait window capture
    public void DragGhost_CapsTheLongestEdgeWhicheverItIs(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = new System.Windows.Media.Imaging.WriteableBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        var ghost = DragPreview.Fit(source, 1d);

        Assert.NotNull(ghost);
        Assert.Equal(expectedWidth, ghost!.PixelWidth);
        Assert.Equal(expectedHeight, ghost.PixelHeight);
        Assert.True(ghost.IsFrozen || ReferenceEquals(ghost, source));
    }

    [Fact]
    public void DragGhost_IsSkippedWhenTheTileHasNoThumbnailYet() =>
        Assert.Null(DragPreview.Fit(null, 1d));

    /// <summary>
    /// The tile frame is 260 DIP, so a fixed 180 px decode was an upscale at every scale the app
    /// runs at. It follows the monitor now — capped at 2x, because a page is 200 live bitmaps and
    /// a 300 % monitor would otherwise put the grid past 300 MB of thumbnails.
    /// </summary>
    [Theory]
    [InlineData(1.0, 260)]
    [InlineData(1.25, 325)]
    [InlineData(1.5, 390)]
    [InlineData(2.0, 520)]
    [InlineData(3.0, 520)]   // capped
    [InlineData(0, 260)]     // an unreadable scale falls back to 1.0 rather than collapsing
    public void TileThumbnail_DecodesForTheMonitorItIsShownOn(double dpiScale, int expectedPixels) =>
        Assert.Equal(expectedPixels, HistoryWindow.TileThumbnailPixelWidth(dpiScale));

    /// <summary>DragPreview divides the ghost's pixel size by the DPI scale to get its logical
    /// size, so the cap has to be in real pixels: 180 logical px on screen at every scale, and
    /// the same 180 whether the drag started in the editor or in History.</summary>
    [Theory]
    [InlineData(1.0, 180)]
    [InlineData(1.25, 225)]
    [InlineData(1.5, 270)]
    [InlineData(2.0, 360)]
    [InlineData(0, 180)]     // an unreadable scale falls back to 1.0 rather than collapsing
    public void DragGhost_IsTheSameLogicalSizeAtEveryScale(double dpiScale, int expectedPixels)
    {
        Assert.Equal(expectedPixels, DragPreview.MaxEdgePixels(dpiScale));

        var landscape = new System.Windows.Media.Imaging.WriteableBitmap(
            1600, 900, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        Assert.Equal(expectedPixels, DragPreview.Fit(landscape, dpiScale)!.PixelWidth);

        var scrolling = new System.Windows.Media.Imaging.WriteableBitmap(
            1200, 9000, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        Assert.Equal(expectedPixels, DragPreview.Fit(scrolling, dpiScale)!.PixelHeight);
    }

    [Fact]
    public void ThumbnailDecode_RunsSeveralAtOnceWithoutTakingTheWholePool() =>
        Assert.InRange(HistoryWindow.ThumbnailDecodeParallelism, 2, 6);

    [Fact]
    public void StillImageTile_HasNoMediaBar()
    {
        var item = new HistoryItem("20260803-120000-000.png");

        Assert.Equal(Visibility.Collapsed, item.MediaBarVisibility);
        Assert.Equal(Visibility.Collapsed, item.MediaFallbackVisibility);
    }

    [Fact]
    public void SourceAppTag_DoesNotDisturbTheTimestampInFront()
    {
        Assert.Equal(
            new DateTime(2026, 8, 3, 12, 0, 0, 0),
            new HistoryItem("20260803-120000-000-scroll@chrome.png").CapturedAt);
    }

    [Theory]
    [InlineData("chrome", "@chrome")]
    [InlineData("  Code  ", "@Code")]
    // A process name cannot reintroduce the separator, a path, or a second extension.
    [InlineData("we@ird", "@weird")]
    [InlineData(@"a\b/c:d", "@abcd")]
    // Dots stay: "Microsoft.Photos" has to round-trip or the badge lookup never finds the
    // process. Only a trailing one goes, because Windows will not keep such a file name.
    [InlineData("my.app", "@my.app")]
    [InlineData("Microsoft.Photos", "@Microsoft.Photos")]
    [InlineData("trailing.", "@trailing")]
    [InlineData("...", "")]
    // Nothing worth badging: no owner, or WinShot looking at itself.
    [InlineData("Desktop", "")]
    [InlineData("winshot", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SourceAppSuffix_IsSafeToPutInAFileName(string? app, string expected)
    {
        string suffix = HistoryService.SourceAppSuffix(app);

        Assert.Equal(expected, suffix);
        Assert.DoesNotContain(suffix, Path.GetInvalidFileNameChars().Select(c => c.ToString()));
    }

    [Fact]
    public void Add_StampsTheSourceAppIntoTheFileName()
    {
        string dir = CreateTempDir();
        try
        {
            using var bitmap = new Bitmap(2, 2);
            string path = CreateService(dir).Add(bitmap, HistoryCaptureKind.Scrolling, "chrome")!;

            Assert.EndsWith("-scroll@chrome.png", path, StringComparison.Ordinal);
            var item = new HistoryItem(path);
            Assert.True(item.IsScrollingCapture);
            Assert.Equal("chrome", item.SourceApp);
        }
        finally
        {
            DeleteTempDir(dir);
        }
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
