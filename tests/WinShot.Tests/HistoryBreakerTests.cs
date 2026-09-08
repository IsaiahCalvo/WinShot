using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using WinShot.History;
using WinShot.SettingsUi;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The edge cases the review panel's breaker lens walks History through: an old settings.json,
/// history switched off, an empty library, a 5,000-file one, deleting every visible tile, a
/// reflow cancelled mid-entrance, a degenerate drag ghost, rename and source-app suffix inputs
/// that must never produce an unreadable file name, a tile that can never decode, duration
/// boundaries, and a capture that lands under a filter hiding it.
/// </summary>
public class HistoryBreakerTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- P1: an old settings.json with none of this track's keys ----
    [Fact]
    public void P1_OldSettingsJson_MissingHistoryKeys_LoadsDefaults()
    {
        string json = "{ \"SaveFolder\": \"C:\\\\Pics\", \"HistoryLimit\": 50, \"HistoryRetentionDays\": 7, " +
                      "\"PostCaptureAction\": \"overlay\", \"HotkeyCaptureRegion\": \"Ctrl+Shift+1\" }";
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        Assert.True(loaded.HistoryEnabled);
        Assert.Equal("Ctrl+Shift+0", loaded.HotkeyHistory);
        Assert.Equal(7, loaded.HistoryRetentionDays);
        int index = SettingsWindow.RetentionToSliderIndex(loaded.HistoryEnabled, loaded.HistoryRetentionDays);
        Assert.Equal(7, SettingsWindow.SliderIndexToRetentionDays(index));
    }

    // ---- P2: the "Never" stop round-trips through Settings ----
    // -1 is the slider's own sentinel for "history off"; OnSave carries it as
    // HistoryEnabled = false and clamps the day count, so no consumer of
    // HistoryRetentionDays ever sees it.
    [Fact]
    public void P2_NeverStop_IsCarriedByHistoryEnabledNotByTheDayCount()
    {
        Assert.Equal(SettingsWindow.HistoryOffStop, SettingsWindow.SliderIndexToRetentionDays(0));
        Assert.Equal(0, SettingsWindow.RetentionToSliderIndex(historyEnabled: false, days: 0));
        Assert.NotEqual(0, SettingsWindow.RetentionToSliderIndex(historyEnabled: true, days: 0));
    }

    // ---- P3: history switched off ----
    [Fact]
    public void P3_HistoryOff_AddAndAddFileWriteNothing()
    {
        string dir = TempDir();
        var settings = new SettingsService();
        settings.Current.HistoryEnabled = false;
        var svc = new HistoryService(settings, () => dir);
        using var bmp = new System.Drawing.Bitmap(8, 8);
        Assert.Null(svc.Add(bmp));
        string src = Path.Combine(TempDir(), "clip.mp4");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        Assert.Null(svc.AddFile(src));
        Assert.Empty(Directory.GetFiles(dir));

        settings.Current.HistoryEnabled = true;
        Assert.NotNull(svc.Add(bmp));
        Assert.Single(Directory.GetFiles(dir));
    }

    // ---- P4: empty library ----
    [Fact]
    public void P4_EmptyLibrary_DiffAndPagingAreQuiet()
    {
        (List<HistoryItem> items, HashSet<HistoryItem> live) =
            HistoryWindow.DiffItems(Array.Empty<string>(), Array.Empty<HistoryItem>());
        Assert.Empty(items);
        Assert.Empty(live);

        var paging = new HistoryIncrementalCollection();
        Assert.Null(paging.ReplaceAll(items, _ => true, null));
        Assert.False(paging.HasMore);
        Assert.Equal(0, paging.VisibleCount);
        paging.Clear();
        Assert.Equal(0, paging.TotalCount);
    }

    // ---- P5: 5,000-file library ----
    [Fact]
    public void P5_HugeLibrary_StaysBoundedAndOrdered()
    {
        string dir = TempDir();
        var settings = new SettingsService();
        settings.Current.HistoryLimit = 6000;
        var svc = new HistoryService(settings, () => dir);
        var baseTime = new DateTime(2026, 1, 1);
        for (int i = 0; i < 5000; i++)
        {
            string name = baseTime.AddSeconds(i).ToString("yyyyMMdd-HHmmss") + "-000.png";
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 0 });
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<string> files = svc.GetItems();
        long listMs = clock.ElapsedMilliseconds;
        Assert.Equal(5000, files.Count);

        clock.Restart();
        (List<HistoryItem> items, _) = HistoryWindow.DiffItems(files, Array.Empty<HistoryItem>());
        long diffMs = clock.ElapsedMilliseconds;

        var paging = new HistoryIncrementalCollection();
        paging.ReplaceAll(items, _ => true, null);
        Assert.Equal(200, paging.VisibleCount);
        Assert.True(paging.HasMore);
        Assert.True(items[0].CapturedAt > items[items.Count - 1].CapturedAt, "newest first");

        clock.Restart();
        (List<HistoryItem> again, _) = HistoryWindow.DiffItems(files, items);
        long reuseMs = clock.ElapsedMilliseconds;
        for (int i = 0; i < items.Count; i++)
            Assert.Same(items[i], again[i]);
        Assert.True(listMs + diffMs + reuseMs < 60_000, $"list {listMs} diff {diffMs} reuse {reuseMs}");
    }

    // ---- P6: delete every visible tile one at a time ----
    [Fact]
    public void P6_RemoveEveryItem_BackfillsAndEmpties()
    {
        var items = Enumerable.Range(0, 250)
            .Select(i => new HistoryItem($@"C:\h\20260101-{i:0000}00-000.png"))
            .ToList();
        var paging = new HistoryIncrementalCollection();
        paging.ReplaceAll(items, _ => true, null);
        Assert.Equal(200, paging.VisibleCount);
        foreach (HistoryItem item in items.ToList())
            paging.Remove(item);
        Assert.Equal(0, paging.VisibleCount);
        Assert.Equal(0, paging.TotalCount);
        Assert.False(paging.HasMore);
    }

    // ---- P7: a tile removed off the visible page ----
    [Fact]
    public void P7_RemoveAnItemThatIsNotVisible_KeepsThePageIntact()
    {
        var items = Enumerable.Range(0, 250)
            .Select(i => new HistoryItem($@"C:\h\20260101-{i:0000}00-000.png"))
            .ToList();
        var paging = new HistoryIncrementalCollection();
        paging.ReplaceAll(items, _ => true, null);
        HistoryItem offPage = paging.AllItems[240];
        Assert.DoesNotContain(offPage, paging.VisibleItems);
        paging.Remove(offPage);
        Assert.Equal(200, paging.VisibleCount);
        Assert.Equal(249, paging.TotalCount);
    }

    // ---- P8: ReflowWrapPanel cancelled mid-entrance ----
    [Fact]
    public void P8_ReflowCancelledMidEntrance_LeavesNoStuckOpacityOrHolder() => OnSta(() =>
    {
        using var _ = new Anim(true);
        int before = Motion.Holders;
        var panel = new ReflowWrapPanel { Width = 400, Height = 400 };
        var a = new Border { Width = 100, Height = 100 };
        panel.Children.Add(a);
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));

        var b = new Border { Width = 100, Height = 100 };
        panel.Children.Insert(0, b);
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));
        Assert.True(panel.IsReflowing, "expected the entrance tween to be live");
        Assert.Equal(0d, b.Opacity);

        panel.CancelReflow();
        Assert.False(panel.IsReflowing);
        Assert.Equal(before, Motion.Holders);

        // A genuine later arrange (a third card arriving) has to rescue the card the cancel
        // left invisible — otherwise a cancelled entrance is a permanently blank tile.
        var c = new Border { Width = 100, Height = 100 };
        panel.Children.Add(c);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));
        Assert.Equal(1d, b.Opacity);
        panel.CancelReflow();
        Assert.Equal(before, Motion.Holders);
    });

    // ---- P8b: a second entrance landing on top of a live one ----
    [Fact]
    public void P8b_EntranceInterruptedByAnotherEntrance_BothCardsEndVisible() => OnSta(() =>
    {
        using var _ = new Anim(true);
        int before = Motion.Holders;
        var panel = new ReflowWrapPanel { Width = 400, Height = 400 };
        panel.Children.Add(new Border { Width = 100, Height = 100 });
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));

        var b = new Border { Width = 100, Height = 100 };
        panel.Children.Add(b);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));
        Assert.Equal(0d, b.Opacity);

        var c = new Border { Width = 100, Height = 100 };
        panel.Children.Add(c);
        panel.InvalidateMeasure();
        panel.InvalidateArrange();
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));

        Assert.Equal(1d, b.Opacity);
        Assert.Equal(0d, c.Opacity);
        panel.CancelReflow();
        Assert.Equal(before, Motion.Holders);
    });

    // ---- P9: reflow with animations off leaves nothing running ----
    [Fact]
    public void P9_ReflowWithAnimationsOff_AppliesEndStateInline() => OnSta(() =>
    {
        using var _ = new Anim(false);
        int before = Motion.Holders;
        var panel = new ReflowWrapPanel { Width = 400, Height = 400 };
        var a = new Border { Width = 100, Height = 100 };
        var b = new Border { Width = 100, Height = 100 };
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));
        panel.Children.Remove(a);
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 400));
        Assert.False(panel.IsReflowing);
        Assert.Equal(1d, b.Opacity);
        Assert.Equal(before, Motion.Holders);
    });

    // ---- P10: drag ghost from a degenerate / huge bitmap ----
    [Fact]
    public void P10_DragGhostFit_HandlesDegenerateAndHugeSources() => OnSta(() =>
    {
        Assert.Null(DragPreview.Fit(null, 1.5));
        var wide = new WriteableBitmap(9000, 12, 96, 96, PixelFormats.Bgra32, null);
        wide.Freeze();
        BitmapSource? fitted = DragPreview.Fit(wide, 3.0);
        Assert.NotNull(fitted);
        Assert.True(Math.Max(fitted!.PixelWidth, fitted.PixelHeight) <= DragPreview.MaxEdgePixels(3.0) + 1,
            $"{fitted.PixelWidth}x{fitted.PixelHeight} vs {DragPreview.MaxEdgePixels(3.0)}");
        Assert.True(fitted.PixelWidth >= 1 && fitted.PixelHeight >= 1);
    });

    // ---- P11: rename edge cases ----
    [Theory]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(".")]
    public void P11_RenameRefusesBlankOrDotOnlyNames(string requested)
    {
        HistoryRename.Result result = HistoryRename.Resolve(@"C:\h\20260101-120000-000.png", requested, _ => false);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void P11b_SuggestedNameIsAlwaysAcceptedByResolve()
    {
        string[] names =
        {
            @"C:\h\20260101-120000-000.png",
            @"C:\h\20260101-120000-000-scroll@chrome.png",
            @"C:\h\@chrome.png",
            @"C:\h\-scroll.png",
            @"C:\h\....png",
            @"C:\h\   .png",
            @"C:\h\@.png",
            @"C:\h\a@.png",
            @"C:\h\-scroll@chrome.png",
        };
        foreach (string path in names)
        {
            string suggestion = HistoryRename.SuggestedName(path);
            HistoryRename.Result result = HistoryRename.Resolve(path, suggestion, _ => false);
            Assert.True(result.Error is null, $"{path} -> '{suggestion}' -> {result.Error}");
        }
    }

    // ---- P12: source-app suffix cannot produce an unreadable name ----
    [Theory]
    [InlineData("@@@", "")]
    [InlineData("....", "")]
    [InlineData("con", "@con")]
    [InlineData("a b", "@a b")]
    [InlineData("ThisIsAVeryLongProcessNameIndeedBeyondThirtyTwo", "@ThisIsAVeryLongProcessNameIndeed")]
    public void P12_SourceAppSuffix(string app, string expected)
    {
        string suffix = HistoryService.SourceAppSuffix(app);
        Assert.Equal(expected, suffix);
        if (suffix.Length > 0)
        {
            var probe = new HistoryItem($@"C:\h\20260101-120000-000{suffix}.png");
            Assert.Equal(expected.Substring(1), probe.SourceApp);
            Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0), probe.CapturedAt);
        }
    }

    // ---- P13: a tile that can never decode ----
    [Fact]
    public void P13_UndecodableFile_SettlesAndIsNotReusedOnceTheFileChanges()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "20260101-120000-000.png");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });
        var item = new HistoryItem(path);
        item.ThumbnailTried = true;
        Assert.Equal(Visibility.Visible, item.UnreadableVisibility);
        Assert.Equal(Visibility.Collapsed, item.ShimmerVisibility);

        (List<HistoryItem> same, _) = HistoryWindow.DiffItems(new[] { path }, new[] { item });
        Assert.Same(item, same[0]);

        Thread.Sleep(30);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        (List<HistoryItem> rebuilt, _) = HistoryWindow.DiffItems(new[] { path }, new[] { item });
        Assert.NotSame(item, rebuilt[0]);
        Assert.False(rebuilt[0].ThumbnailTried);
    }

    // ---- P14: duration boundaries ----
    [Theory]
    [InlineData(0, "")]
    [InlineData(-5, "")]
    [InlineData(59.4, "1:00")]
    [InlineData(3599.5, "1:00:00")]
    public void P14_DurationLabelBoundaries(double seconds, string expected) =>
        Assert.Equal(expected, HistoryItem.FormatDuration(TimeSpan.FromSeconds(seconds)));

    // ---- P15: a capture landing while a filter chip hides it ----
    [Fact]
    public void P15_FilterHidesTheNewCapture_SelectionAndCountsStayHonest()
    {
        var images = Enumerable.Range(0, 3)
            .Select(i => new HistoryItem($@"C:\h\2026010{i + 1}-120000-000.png"))
            .ToList();
        var paging = new HistoryIncrementalCollection();
        paging.ReplaceAll(images, item => item.IsImage, images[0].FilePath);
        Assert.Equal(3, paging.VisibleCount);

        var withVideo = new List<HistoryItem>(images) { new(@"C:\h\20260104-120000-000.mp4") };
        HistoryItem? restored = paging.ReplaceAll(withVideo, item => item.IsImage, images[0].FilePath);
        Assert.Same(images[0], restored);
        Assert.Equal(3, paging.VisibleCount);
        Assert.Equal(4, paging.TotalCount);
        Assert.Equal(3, paging.FilteredCount);
    }
}
