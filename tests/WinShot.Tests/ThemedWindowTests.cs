using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Editor;
using WinShot.Editor.Background;
using WinShot.History;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Recording;
using WinShot.Scrolling;
using WinShot.SettingsUi;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class ThemedWindowTests
{
    [Fact]
    public void ThemedWindows_LoadSharedThemeOnDemand()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var settings = new SettingsService();
                var history = new HistoryService(settings);

                using var previewFile = new TempImageFile();

                using (var fastSelector = new FastRegionSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()),
                    settings))
                {
                    fastSelector.Show();
                    fastSelector.Close();
                }
                using (var fastAllInOne = new FastAllInOneSelectorDialog(
                    () => Task.FromResult(new List<WindowInfo>()),
                    settings))
                {
                    fastAllInOne.Show();
                    fastAllInOne.Close();
                }
                ShowAndClose(new FastQuickActionsWindow(NewBitmap(), settings));
                QuickActionButtonSmoke(settings);
                ShowAndClose(new FastPinWindow(NewBitmap(), settings));
                ShowAndClose(new FastQuickPreviewWindow(previewFile.Path));
                ShowAndClose(new HistoryWindow(history, settings));
                HistorySelectionInteractionSmoke(history, settings);
                HistoryActivationSmoke(history, settings);
                HistoryThumbnailLoadSmoke(settings);
                HistoryReflowSmoke(history, settings);
                HistoryReflowCloseSmoke(history, settings);
                HistoryRotateRefreshSmoke(settings);
                HistoryOverlayActionsBaseStateSmoke(history, settings);
                HistoryCaptureRoundTripRefreshSmoke(settings);
                ShowAndClose(new SettingsWindow(settings));
                SettingsWindowShowCloseSmoke(settings);
                HistoryWindowShowCloseSmoke(history, settings);
                ShowAndClose(new FastSelfTimerWindow(1));
                ShowAndClose(new FastRecordingOptionsDialog(settings.Current));
                ShowAndClose(new FastRecordingControlBar());
                ShowAndClose(new FastRecordingCountdownWindow(1, new SD.Rectangle(0, 0, 80, 60)));
                ShowAndClose(FastQuickActionsWindow.CreateForMediaFile(
                    previewFile.Path, NewBitmap(), settings, canEdit: true));
                ShowAndClose(FastClickHighlightOverlayWindow.CreateForSmokeTest(new SD.Rectangle(0, 0, 80, 60)));
                ShowAndClose(FastKeystrokeOverlayWindow.CreateForSmokeTest(new SD.Rectangle(0, 0, 80, 60)));
                ShowAndClose(new ResizeDialog(80, 50));
                VideoEditorResolutionSmoke(previewFile.Path, settings, history);
                ShowAndClose(new GifEditorWindow(previewFile.Path, settings, history));
                ShowAndClose(new RecordingRecoveryWindow(new List<RecoverableRecordingTempFile>
                {
                    new(previewFile.Path, ".mp4", 1024, DateTime.UtcNow),
                }));
                ShowAndClose(ThemedMessageDialog.Build(null!, "Delete item", "Body text").Dialog);

                CreatePrivateForm<FastDisplayPickerDialog>().Close();
                ShowAndClose(new ScrollDimOverlay(new SD.Rectangle(20, 20, 160, 120)));
                ShowAndClose(new ScrollControlsBar(new SD.Rectangle(20, 20, 160, 120)));
                ShowAndClose(new ScrollPreviewPanel(new SD.Rectangle(20, 20, 160, 120)));

                var editor = new EditorWindow(NewBitmap(), settings, history);
                editor.Show();
                EditorShellContractSmoke(editor);
                RedactionObjectSmoke(editor);
                MagnifierZoomUndoSmoke(editor);
                EyedropperPickerLatchSmoke(editor);
                AnnotationClipboardSmoke(editor);
                TextAndCounterSmoke(editor);
                ToolRowStabilitySmoke(editor);
                RotationGripSmoke(editor);
                ZOrderReorderSmoke(editor);
                SaveProjectSmoke(editor, settings, history);
                PinSmoke(editor);
                BackgroundFromEditorSmoke(editor);
                editor.Close();

                var composer = new BackgroundComposerWindow(NewBitmap(), settings, history);
                composer.Show();
                composer.Close();

                EditorFitsTallImageSmoke(settings, history);
                EditorGrowLandsOnItsTargetSmoke(settings, history);
                EditorFitsSmallImageSmoke(settings, history);

                app.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static void ShowAndClose(Window window)
    {
        window.Show();
        window.Close();
    }

    /// <summary>
    /// The video editor's resolution picker has to offer every preset for the source it is
    /// looking at. 1920x1080 is the size that used to fail: the field seeds itself with
    /// <c>For(1920, 1080)</c>, the old "skip when the list has not changed" guard compared the
    /// resolved presets against that seed rather than against the combo, and the combo kept the
    /// single "Original" placeholder the XAML declares — so 75 %, 50 % and 720p were unreachable
    /// on the most common recording size there is. Lives here because it needs a real window,
    /// and this test owns the assembly's one <see cref="Application"/>.
    /// </summary>
    private static void VideoEditorResolutionSmoke(string path, SettingsService settings, HistoryService history)
    {
        var window = new VideoEditorWindow(path, settings, history);
        window.Show();

        // The call OnMediaOpened makes. The stub source reports 0x0, and For(0, 0) falls back to
        // the same 1920x1080 list the field is seeded with — the collision the old guard tripped on.
        typeof(VideoEditorWindow)
            .GetMethod("PopulateResolutions", NP)!
            .Invoke(window, null);
        var combo = (ComboBox)typeof(VideoEditorWindow).GetField("ResolutionCombo", NP)!.GetValue(window)!;

        Assert.Equal(
            VideoResolutionPresets.For(1920, 1080).Select(preset => preset.Label),
            combo.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content));

        window.Close();
    }

    private const System.Reflection.BindingFlags NP =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static void HistorySelectionInteractionSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        var paging = (HistoryIncrementalCollection)typeof(HistoryWindow).GetField("_paging", NP)!.GetValue(window)!;
        var items = Enumerable.Range(0, 5)
            .Select(index => new HistoryItem($@"C:\sanitized-history\20260803-12000{index}-000.png"))
            .ToList();
        paging.ReplaceAll(items, static _ => true, selectedPath: null);
        window.Show();
        window.UpdateLayout();

        try
        {
            ((Button)window.FindName("SelectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var applyClick = typeof(HistoryWindow).GetMethod("ApplySelectionClick", NP)!;
            var list = (ListBox)window.FindName("ItemsList");

            applyClick.Invoke(window, new object[] { items[0], ModifierKeys.None });
            applyClick.Invoke(window, new object[] { items[2], ModifierKeys.None });
            Assert.Equal(2, list.SelectedItems.Count);

            applyClick.Invoke(window, new object[] { items[1], ModifierKeys.Control });
            applyClick.Invoke(window, new object[] { items[0], ModifierKeys.Control });
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.DoesNotContain(items[0], list.SelectedItems.Cast<HistoryItem>());

            applyClick.Invoke(window, new object[] { items[4], ModifierKeys.Shift });
            Assert.Equal(5, list.SelectedItems.Count);
            applyClick.Invoke(window, new object[] { items[1], ModifierKeys.Shift });
            Assert.Equal(3, list.SelectedItems.Count);

            // One button does the whole gesture, and says which half it will do next.
            var selectAll = (Button)window.FindName("AllSelectionButton");
            var selectAllText = (TextBlock)window.FindName("SelectAllText");
            Assert.Equal("Select all", selectAllText.Text);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(5, list.SelectedItems.Count);
            Assert.Equal("Clear selection", selectAllText.Text);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(list.SelectedItems.Cast<HistoryItem>());
            Assert.Equal("Select all", selectAllText.Text);
            selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(5, list.SelectedItems.Count);

        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Enter restores a still image to the Quick Access card and leaves History open;
    /// an MP4 has no card, so Enter must not raise the restore handoff.</summary>
    private static void HistoryActivationSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var paging = (HistoryIncrementalCollection)typeof(HistoryWindow).GetField("_paging", NP)!.GetValue(window)!;
        var items = new List<HistoryItem>
        {
            new(@"C:\sanitized-history\20260803-120000-000.png"),
            new(@"C:\sanitized-history\20260803-120001-000.mp4"),
        };
        paging.ReplaceAll(items, static _ => true, selectedPath: null);
        window.Show();
        window.UpdateLayout();

        try
        {
            var restored = new List<string>();
            window.RestoreRequested += restored.Add;
            var list = (ListBox)window.FindName("ItemsList");

            list.SelectedItem = items[0];
            Assert.True(window.HandleKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(new[] { items[0].FilePath }, restored);
            Assert.True(window.IsVisible);

            // Enter on the same image again stacks a second card rather than closing History.
            Assert.True(window.HandleKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(2, restored.Count);

            list.SelectedItem = items[1];
            Assert.True(window.HandleKey(Key.Enter, ModifierKeys.None));
            Assert.Equal(2, restored.Count);

            // The video tile wears a bar across the whole card, not a badge in one corner: the
            // clip length is what tells two recordings apart.
            System.Windows.Controls.Border? mediaBar = null;
            WaitUntil(() =>
            {
                list.UpdateLayout();
                mediaBar = list.ItemContainerGenerator.ContainerFromItem(items[1]) is FrameworkElement card
                    ? FindVisualNamed<System.Windows.Controls.Border>(card, "MediaBar")
                    : null;
                return mediaBar is { ActualWidth: > 200 };
            });
            Assert.NotNull(mediaBar);
            Assert.Equal(System.Windows.Visibility.Visible, mediaBar!.Visibility);
            Assert.True(
                mediaBar.ActualWidth > 200,
                $"the media bar should span the card, not sit in a corner: {mediaBar.ActualWidth}");

            // Ctrl+C claims the key for a still image and leaves it alone for a video.
            list.SelectedItem = items[0];
            Assert.True(window.HandleKey(Key.C, ModifierKeys.Control));
            list.SelectedItem = items[1];
            Assert.False(window.HandleKey(Key.C, ModifierKeys.Control));
            Assert.False(window.HandleKey(Key.C, ModifierKeys.None));

            // Rotate, Rename, Print and Open-with are only on the tile's context menu, and that
            // menu hangs off the card inside the item template - so the Menu key, which looks up
            // from the focused CONTAINER, found nothing until the grid opened it explicitly.
            list.SelectedItem = items[0];
            list.UpdateLayout();
            Assert.True(window.HandleKey(Key.Apps, ModifierKeys.None));
            var opened = (System.Windows.Controls.ContextMenu?)null;
            if (list.ItemContainerGenerator.ContainerFromItem(items[0]) is FrameworkElement tile)
                opened = FindVisualNamed<System.Windows.Controls.Border>(tile, "Card")?.ContextMenu;
            Assert.NotNull(opened);
            Assert.True(opened!.IsOpen, "the Menu key should open the tile's own context menu");
            opened.IsOpen = false;
            Assert.True(window.HandleKey(Key.F10, ModifierKeys.Shift));
            opened.IsOpen = false;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every card on a page ends up with a decoded thumbnail. The decode now runs
    /// several files at a time and marshals each result back to the UI thread, so this also
    /// guards the shape that would deadlock if it were ever awaited from the UI thread.</summary>
    private static void HistoryThumbnailLoadSmoke(SettingsService settings)
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            for (int index = 0; index < 12; index++)
            {
                using var bitmap = new SD.Bitmap(40, 30);
                using (var graphics = SD.Graphics.FromImage(bitmap))
                    graphics.Clear(SD.Color.FromArgb(20 + (index * 5), 60, 90));
                bitmap.Save(
                    System.IO.Path.Combine(dir, $"20260803-1200{index:00}-000.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }

            var scoped = new HistoryService(settings, () => dir);
            var window = new HistoryWindow(scoped, settings);
            window.Show();
            var list = (ListBox)window.FindName("ItemsList");
            // Showing the window starts its own refresh; wait it out so the explicit reload
            // below is the only one in flight and its task really is the one to await.
            WaitForCondition(
                () => list.Items.Count == 12 && list.Items.Cast<HistoryItem>().All(i => i.Thumbnail is not null),
                "the twelve captures should be on screen with a decoded thumbnail each");

            const System.Reflection.BindingFlags NP =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var reload = (Task)typeof(HistoryWindow).GetMethod("ReloadAsync", NP)!.Invoke(window, null)!;
            WaitForTask(reload);
            reload.GetAwaiter().GetResult();

            Assert.Equal(12, list.Items.Count);
            Assert.All(list.Items.Cast<HistoryItem>(), item => Assert.NotNull(item.Thumbnail));

            // A capture lands while the window is open (the folder watcher's refresh, a rotate,
            // or re-opening the window): the twelve files that are still there must keep the very
            // same HistoryItem, decoded thumbnail and all. Rebuilding them made every visible tile
            // a removal plus an insertion, so the whole grid blanked to shimmer and faded back in
            // for one new file.
            var before = list.Items.Cast<HistoryItem>().ToDictionary(item => item.FilePath);
            using (var extra = new SD.Bitmap(40, 30))
                extra.Save(
                    System.IO.Path.Combine(dir, "20260803-120099-000.png"),
                    System.Drawing.Imaging.ImageFormat.Png);

            var again = (Task)typeof(HistoryWindow).GetMethod("ReloadAsync", NP)!.Invoke(window, null)!;
            WaitForTask(again);
            again.GetAwaiter().GetResult();

            Assert.Equal(13, list.Items.Count);
            foreach (HistoryItem item in list.Items.Cast<HistoryItem>())
            {
                if (!before.TryGetValue(item.FilePath, out HistoryItem? kept)) continue;
                Assert.Same(kept, item);
                Assert.NotNull(item.Thumbnail);
            }

            window.Close();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A rotate rewrites a history file under its own name. The refresh keeps the HistoryItem
    /// behind any path that is still there — right for a folder that gained a file, wrong here,
    /// because the item carries the thumbnail decoded from the pixels the rotate replaced. The
    /// tile used to stay landscape for the life of the window.
    /// </summary>
    private static void HistoryRotateRefreshSmoke(SettingsService settings)
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string file = System.IO.Path.Combine(dir, "20260803-120000-000.png");
            using (var bitmap = new SD.Bitmap(200, 60))
            {
                using (var graphics = SD.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(SD.Color.Teal);
                    graphics.FillRectangle(SD.Brushes.White, 4, 4, 60, 18);
                }

                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }

            var scoped = new HistoryService(settings, () => dir);
            var window = new HistoryWindow(scoped, settings);
            window.Show();
            var list = (ListBox)window.FindName("ItemsList");
            // Let the show-refresh finish; it would otherwise cancel the explicit reload below.
            WaitForCondition(
                () => list.Items.Count == 1 && ((HistoryItem)list.Items[0]!).Thumbnail is not null,
                "the fixture capture should be on screen with a decoded thumbnail");

            const System.Reflection.BindingFlags NP =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var reload = (Task)typeof(HistoryWindow).GetMethod("ReloadAsync", NP)!.Invoke(window, null)!;
            WaitForTask(reload);
            reload.GetAwaiter().GetResult();

            Assert.Single(list.Items);
            var before = (System.Windows.Media.Imaging.BitmapSource)((HistoryItem)list.Items[0]!).Thumbnail!;
            Assert.True(before.PixelWidth > before.PixelHeight, "the fixture should start landscape");

            // Exactly what OnRotate does: rotate, stage outside the folder, move over the top.
            using (var image = HistoryImageLoader.LoadDetachedBitmap(file))
            {
                image.RotateFlip(SD.RotateFlipType.Rotate90FlipNone);
                string staging = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"winshot-test-{Guid.NewGuid():N}.png");
                ImageSaver.Save(image, staging);
                System.IO.File.Move(staging, file, overwrite: true);
            }

            var again = (Task)typeof(HistoryWindow).GetMethod("ReloadAsync", NP)!.Invoke(window, null)!;
            WaitForTask(again);
            again.GetAwaiter().GetResult();

            var after = (System.Windows.Media.Imaging.BitmapSource)((HistoryItem)list.Items[0]!).Thumbnail!;
            Assert.NotSame(before, after);
            Assert.True(
                after.PixelHeight > after.PixelWidth,
                $"the rotated tile should read portrait, was {after.PixelWidth}x{after.PixelHeight}");

            window.Close();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The hover action row's TEMPLATE base has to be the shown state. A container generated while
    /// the pointer is already inside its grid slot evaluates the "neither hovered nor focused"
    /// condition as false on its very first pass, and WPF runs neither EnterActions nor ExitActions
    /// on an initial evaluation — so the tile keeps whatever the base value says until the pointer
    /// leaves and comes back. With a hidden base that was an undrawn, fully clickable Delete button
    /// across the bottom 56 DIP of the tile. The hidden state belongs in setters, which do apply on
    /// the first evaluation, and hit-testing has to travel with it.
    /// </summary>
    private static void HistoryOverlayActionsBaseStateSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var paging = (HistoryIncrementalCollection)typeof(HistoryWindow).GetField("_paging", NP)!.GetValue(window)!;
        paging.ReplaceAll(
            new List<HistoryItem> { new(@"C:\sanitized-history\20260803-120000-000.png") },
            static _ => true,
            selectedPath: null);
        window.Show();
        window.UpdateLayout();

        try
        {
            var list = (ListBox)window.FindName("ItemsList");

            var bare = (FrameworkElement)list.ItemTemplate.LoadContent();
            var baseRow = (Border)LogicalTreeHelper.FindLogicalNode(bare, "OverlayActions")!;
            Assert.Equal(1d, baseRow.Opacity);
            Assert.True(baseRow.IsHitTestVisible);

            // And the at-rest tile still hides it, from the setters rather than the storyboard.
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)!;
            container.ApplyTemplate();
            window.UpdateLayout();
            var restingRow = (Border)FindDescendantByName(container, "OverlayActions")!;
            Assert.Equal(0d, restingRow.Opacity);
            Assert.False(restingRow.IsHitTestVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            FrameworkElement? hit = FindDescendantByName(
                System.Windows.Media.VisualTreeHelper.GetChild(root, index), name);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// "New capture" from the empty state hides History, and hiding stops the folder watcher — so
    /// the capture the button just asked for is written while nothing is listening, and Show()
    /// builds a fresh watcher that by definition knows nothing about it. Coming back has to
    /// re-read the folder, or the grid stays on "No captures yet" behind a card holding the shot.
    /// </summary>
    private static void HistoryCaptureRoundTripRefreshSmoke(SettingsService settings)
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var scoped = new HistoryService(settings, () => dir);
            var window = new HistoryWindow(scoped, settings);
            window.Show();
            var list = (ListBox)window.FindName("ItemsList");
            WaitForCondition(() => list.Items.Count == 0, "the fixture folder starts empty");

            window.HideForCapture();
            using (var bitmap = new SD.Bitmap(120, 80))
            {
                using (var graphics = SD.Graphics.FromImage(bitmap)) graphics.Clear(SD.Color.Teal);
                bitmap.Save(
                    System.IO.Path.Combine(dir, "20260803-120500-000.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }

            window.ShowAfterCapture();
            WaitForCondition(
                () => list.Items.Count == 1,
                "the capture taken while History was hidden should be on the grid when it returns");

            window.Close();
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Closing History on top of a running slide hands the high-resolution clock back
    /// straight away, rather than leaving an orphaned tween to finish on its own.</summary>
    private static void HistoryReflowCloseSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var paging = (HistoryIncrementalCollection)typeof(HistoryWindow).GetField("_paging", NP)!.GetValue(window)!;
        paging.ReplaceAll(
            Enumerable.Range(0, 8)
                .Select(index => new HistoryItem(index % 2 == 0
                    ? $@"C:\sanitized-history6080{index}-120000-000.png"
                    : $@"C:\sanitized-history6080{index}-120000-000.mp4"))
                .ToList(),
            static _ => true,
            selectedPath: null);
        window.Show();
        window.UpdateLayout();

        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        try
        {
            Motion.AnimationsEnabled = () => true;
            int holdersBefore = Motion.Holders;
            var panel = FindVisualChild<ReflowWrapPanel>((ListBox)window.FindName("ItemsList"));
            Assert.NotNull(panel);

            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ChipImages")).IsChecked = true;
            window.UpdateLayout();
            Assert.True(panel!.IsReflowing, "the slide should still be running when the window closes");

            window.Close();

            Assert.False(panel.IsReflowing);
            Assert.Equal(holdersBefore, Motion.Holders);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
        }
    }

    /// <summary>Filtering the grid slides the survivors into their new slots instead of
    /// rebuilding the page, and the tween lets go of the high-resolution clock when it ends.</summary>
    private static void HistoryReflowSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var paging = (HistoryIncrementalCollection)typeof(HistoryWindow).GetField("_paging", NP)!.GetValue(window)!;
        var items = new List<HistoryItem>
        {
            new(@"C:\sanitized-history\20260803-120000-000.png"),
            new(@"C:\sanitized-history\20260803-120001-000.mp4"),
            new(@"C:\sanitized-history\20260803-120002-000.png"),
            new(@"C:\sanitized-history\20260803-120003-000.mp4"),
            new(@"C:\sanitized-history\20260803-120004-000.png"),
        };
        paging.ReplaceAll(items, static _ => true, selectedPath: null);
        window.Show();
        window.UpdateLayout();

        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        try
        {
            Motion.AnimationsEnabled = () => true;
            var list = (ListBox)window.FindName("ItemsList");
            var panel = FindVisualChild<ReflowWrapPanel>(list);
            Assert.NotNull(panel);
            int holdersBefore = Motion.Holders;

            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ChipImages")).IsChecked = true;
            window.UpdateLayout();

            // The videos leave from the middle of the grid, so the stills behind them slide into
            // the gap rather than being torn down and rebuilt in place.
            Assert.Equal(3, list.Items.Count);
            Assert.True(panel!.LastReflowCount > 0, $"expected cards to slide, moved {panel.LastReflowCount}");
            Assert.True(panel.IsReflowing, "the slide should be running, not already over");

            // With animation off system-wide the same change lands on its end state inside the
            // arrange pass and takes no clock at all.
            Motion.AnimationsEnabled = () => false;
            ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("ChipAll")).IsChecked = true;
            window.UpdateLayout();

            Assert.Equal(5, list.Items.Count);
            Assert.False(panel.IsReflowing);
            Assert.True(
                Motion.Holders <= holdersBefore,
                $"reflow leaked a Motion holder: {holdersBefore} -> {Motion.Holders}");

            // Dragging the window edge re-wraps every row for reasons that have nothing to do
            // with the content, so it must stay silent — the same width test that has to let a
            // filter through when the scrollbar comes or goes with it.
            Motion.AnimationsEnabled = () => true;
            window.Width -= 120;
            window.UpdateLayout();

            Assert.Equal(0, panel.LastReflowCount);
            Assert.False(panel.IsReflowing, "a resize must not start a slide");
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            // A failed assertion must not leave a window open: the STA thread would end with a
            // live WPF window and take the whole test host down with it.
            window.Close();
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }
    }

    private static T? FindVisualNamed<T>(System.Windows.DependencyObject root, string name)
        where T : System.Windows.FrameworkElement
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name) return match;
            if (FindVisualNamed<T>(child, name) is T nested) return nested;
        }

        return null;
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is T nested) return nested;
        }

        return null;
    }

    private static void ShowAndClose(WF.Form form)
    {
        form.Show();
        form.Close();
        form.Dispose();
    }

    private static void SettingsWindowShowCloseSmoke(SettingsService settings)
    {
        settings.Current.ShortcutBindings["legacy-placeholder"] = "Ctrl+Alt+Q";
        settings.Current.ShowCrosshair = false;
        settings.Current.CrosshairMode = "always";
        settings.Current.ScreenshotSave = true;
        settings.Current.ScreenshotShowOverlay = false;
        var window = SettingsWindow.Show(settings);
        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        AssertSettingsTruthfulAndAccessible(window);
        Assert.Equal(
            "never",
            ((System.Windows.Controls.ComboBoxItem)
                ((System.Windows.Controls.ComboBox)window.FindName("CrosshairModeCombo")).SelectedItem).Tag);
        // The after-capture matrix is a set: Save on, Quick Access off, independently.
        Assert.True(((System.Windows.Controls.CheckBox)window.FindName("ScreenshotSaveCheck")).IsChecked);
        Assert.False(((System.Windows.Controls.CheckBox)window.FindName("ScreenshotShowOverlayCheck")).IsChecked);
        SettingsSectionMotionSmoke(window);
        SettingsLiveApplyAndDefaultsSmoke(window, settings);
        typeof(SettingsWindow).GetMethod(
            "SaveShortcutBoxes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, new object[] { settings.Current });
        Assert.Equal("Ctrl+Alt+Q", settings.Current.ShortcutBindings["legacy-placeholder"]);
        window.Close();
        WaitUntilHidden(window);
        Assert.False(window.IsVisible);
    }

    /// <summary>
    /// The Annotate pane shipped Collapsed while four of its settings sat in
    /// VisibleRuntimeBackedProperties, so the catalog promised a control the user could never
    /// reach. The catalog test only partitions the property list; nothing tied a "visible"
    /// property to a pane you can actually select. This does: a section you cannot navigate to
    /// is not a place a setting can live.
    /// </summary>
    private static void AssertEveryVisibleSettingHasAReachableControl(SettingsWindow window)
    {
        var list = (System.Windows.Controls.ListBox)window.FindName("SectionList")!;
        var sections = (System.Windows.Controls.ScrollViewer[])typeof(SettingsWindow)
            .GetMethod("Sections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, null)!;
        Assert.Equal(list.Items.Count, sections.Length);

        var reachable = new HashSet<System.Windows.Controls.ScrollViewer>();
        for (int i = 0; i < sections.Length; i++)
        {
            var tab = (System.Windows.Controls.ListBoxItem)list.Items[i]!;
            Assert.True(
                tab.Visibility == Visibility.Visible,
                $"nav tab {i} ({tab.Name}) is {tab.Visibility} — a collapsed tab can never be selected, " +
                "so everything on its pane is unreachable");
            reachable.Add(sections[i]);
        }

        bool IsReachable(string controlName)
        {
            if (window.FindName(controlName) is not DependencyObject element) return false;
            for (DependencyObject? d = element; d is not null; d = LogicalTreeHelper.GetParent(d))
                if (d is System.Windows.Controls.ScrollViewer sv && reachable.Contains(sv))
                    return true;
            return false;
        }

        string root = TestPaths.RepoRoot();
        var names = new HashSet<string>(
            System.Text.RegularExpressions.Regex
                .Matches(
                    System.IO.File.ReadAllText(System.IO.Path.Combine(
                        root, "src", "WinShot", "SettingsUi", "SettingsWindow.xaml")),
                    @"x:Name=""(?<n>\w+)""")
                .Select(m => m.Groups["n"].Value),
            StringComparer.Ordinal);

        string code = string.Concat(new[] { "SettingsWindow.xaml.cs", "SettingsWindow.Shortcuts.cs" }
            .Select(f => System.IO.File.ReadAllText(
                System.IO.Path.Combine(root, "src", "WinShot", "SettingsUi", f))));
        string[] statements = code.Split(';');

        int matched = 0;
        foreach (string property in SettingsTruthCatalog.VisibleRuntimeBackedProperties)
        {
            // The shortcut rows are built from SettingsWindow.Shortcuts.cs's own table rather
            // than from named XAML, and SettingsTruthCatalogTests already checks that table
            // against the app. The tab-visibility loop above covers the pane they land on.
            if (property.StartsWith("Hotkey", StringComparison.Ordinal)) continue;

            var reader = new System.Text.RegularExpressions.Regex($@"\bs\.{property}\b");
            bool found = statements
                .Where(s => reader.IsMatch(s))
                .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s, @"\b\w+\b").Select(m => m.Value))
                .Any(id => names.Contains(id) && IsReachable(id));
            Assert.True(found, $"{property} is catalogued as a visible runtime-backed setting, but no control " +
                               "bound to it lives on a pane the user can navigate to");
            matched++;
        }

        Assert.True(matched >= 50, $"only {matched} properties were checked — the source scan stopped working");
    }

    private static void AssertSettingsTruthfulAndAccessible(SettingsWindow window)
    {
        AssertEveryVisibleSettingHasAReachableControl(window);

        T Named<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(window.FindName(name));

        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.True(window.MinWidth >= 620);
        Assert.True(window.MinHeight >= 560);

        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.ListBoxItem>("QuickAccessNavItem").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.ListBoxItem>("AnnotateNavItem").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.Border>("AfterCaptureMatrix").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("SoundsRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("TrayIconRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("AskForNameRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("ScreenshotBackgroundRow").Visibility);
        // The shadow switch only means something once a background is on, so it rides the same
        // reveal as the colour field rather than sitting there greyed out under "None".
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.DockPanel>("ScreenshotBackgroundShadowRow").Visibility);
        // The corner trim applies to every window capture, so unlike the backdrop's shadow it
        // has nothing to be revealed by and stands on its own.
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("TrimWindowShadowRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.Border>("NotificationsCard").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("ShutterSoundRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("MaxResolutionRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.Border>("PinnedStyleCard").Visibility);
        // Deleted rather than left Collapsed: a row for a setting nothing implements is dead
        // XAML, and "it is in the file but hidden" is how it comes back by accident.
        foreach (string gone in new[]
        {
            "UnavailableCopyFormatCard", "UnavailableSrgbRow", "UnavailablePixelBorderRow",
            "UnavailableRetinaSuffixRow", "UnavailableResetWarningsCard",
        })
        {
            Assert.Null(window.FindName(gone));
        }

        var done = Named<System.Windows.Controls.Button>("SaveButton");
        var cancel = Named<System.Windows.Controls.Button>("CancelButton");
        Assert.True(done.IsDefault);
        Assert.True(cancel.IsCancel);
        // "Close", not "Cancel". Checkboxes, combos and sliders live-apply 250 ms after they
        // change, so a button labelled Cancel promising to "close without applying changes"
        // was describing the opposite of what it did.
        Assert.Equal("Close", cancel.Content);
        Assert.DoesNotContain(
            "without applying",
            AutomationProperties.GetHelpText(cancel) ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(done.FocusVisualStyle);
        Assert.NotNull(cancel.FocusVisualStyle);

        string[] accessibleControls =
        [
            "StartupCheck", "UpdatesCheck", "SaveFolderBox", "BrowseSaveFolderButton",
            "HideIconsCheck", "ShowTrayIconCheck", "ScreenshotShowOverlayCheck", "RecordingShowOverlayCheck",
            "ScreenshotCopyCheck", "RecordingCopyCheck", "ScreenshotSaveCheck",
            "ScreenshotOpenAnnotateCheck", "ScreenshotPinCheck", "RecordingOpenEditorCheck",
            "OverlayPositionCombo",
            "OverlayMoveToActiveScreenCheck", "OverlaySizeSlider", "OverlayAutoCloseCheck",
            "OverlayActionCombo", "OverlayCloseBox", "OverlayCloseAfterDragCheck", "OverlaySaveBehaviorCombo",
            "ShowRecordingControlsCheck", "ShowRecordingTimerCheck", "ScaleHiDpiVideoCheck", "CaptureCursorCheck",
            "ClickHighlightsCheck", "KeystrokesCheck", "RememberLastSelectionCheck", "DimScreenCheck",
            "CountdownBox", "WebcamCombo", "WebcamSizeBox",
            "MaxResolutionCombo", "RecordingFpsBox", "RecordAudioCheck",
            "SystemAudioCheck", "RecordAudioMonoCheck", "GifFpsBox",
            "GifQualitySlider", "OptimizeGifsCheck", "GifSizeCombo", "FormatCombo",
            "HiDpiCheck", "SelfTimerCombo", "FreezeScreenCheck", "CrosshairModeCombo",
            "ShowMagnifierCheck", "TemplateBox", "HistorySlider", "PinnedRoundedCornersCheck",
            "PinnedShadowCheck", "PinnedBorderCheck", "AllInOneRememberCheck",
            "OcrJoinLinesCheck", "AskForNameCheck",
            "NotifyOcrCombo", "NotifyCopyCombo", "NotifySaveCombo",
            "BackgroundCombo", "BackgroundColorBox", "BackgroundShadowCheck", "TrimWindowShadowCheck",
            "RestoreDefaultsButton", "CancelButton", "SaveButton",
            "AboutCheckUpdatesButton",
            "AboutRepositoryButton", "AboutLogsButton", "AboutDiagnosticsToggle",
            "AboutNoticesToggle", "AboutLicensesButton",
        ];

        foreach (string name in accessibleControls)
        {
            var control = Named<System.Windows.Controls.Control>(name);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), name);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(control)), name);
        }

        var shortcutBoxes = FindLogicalChildren<HotkeyBox>(window).ToArray();
        Assert.Equal(SettingsTruthCatalog.ExecutableShortcutIds.Count, shortcutBoxes.Length);
        Assert.All(shortcutBoxes, box =>
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(box)));
            Assert.NotNull(box.FocusVisualStyle);
        });

        var tabOrder = new[]
        {
            Named<System.Windows.Controls.Control>("StartupCheck").TabIndex,
            Named<System.Windows.Controls.Control>("SaveFolderBox").TabIndex,
            Named<System.Windows.Controls.Control>("ScreenshotShowOverlayCheck").TabIndex,
            Named<System.Windows.Controls.Control>("CaptureCursorCheck").TabIndex,
            Named<System.Windows.Controls.Control>("FormatCombo").TabIndex,
            Named<System.Windows.Controls.Control>("TemplateBox").TabIndex,
            cancel.TabIndex,
            done.TabIndex,
        };
        Assert.Equal(tabOrder.Order().ToArray(), tabOrder);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is T match)
                yield return match;
            if (child is DependencyObject dependencyObject)
                foreach (T descendant in FindLogicalChildren<T>(dependencyObject))
                    yield return descendant;
        }
    }

    /// <summary>The window fades in instead of appearing between two frames, Escape plays that
    /// backwards instead of blinking it away, and with animation off system-wide both land on
    /// their end state immediately.</summary>
    private static void HistoryWindowShowCloseSmoke(HistoryService history, SettingsService settings)
    {
        var window = HistoryWindow.Show(history, settings);
        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);

        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var fadeIn = typeof(HistoryWindow).GetMethod("FadeIn", NP)!;
        void FadeIn(double from) => fadeIn.Invoke(window, new object[] { from });
        Func<bool> previousAnimations = Motion.AnimationsEnabled;
        try
        {
            Motion.AnimationsEnabled = () => false;
            FadeIn(0d);
            Assert.Equal(1d, window.Opacity, 3);

            Motion.AnimationsEnabled = () => true;
            FadeIn(0d);
            Assert.Equal(0d, window.Opacity, 3);

            // A window caught part-way through the entrance ramps on from where it got to rather
            // than restarting from black.
            FadeIn(0.4d);
            Assert.Equal(0.4d, window.Opacity, 3);

            // Escape starts the exit tween, so the window is still there on the next line — and
            // it starts from the opacity the entrance had reached, not from fully opaque.
            Assert.True(window.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.True(window.IsVisible, "Escape should fade the window out, not blink it away");
            Assert.True(
                window.Opacity <= 0.4d,
                $"Escape mid-entrance must not brighten the window first, was {window.Opacity:0.000}");
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            window.Close();
        }

        WaitUntilHidden(window);
        Assert.False(window.IsVisible);

        // With animation off, Escape has to close the window on the spot rather than waiting
        // for a tween that will never run.
        var second = HistoryWindow.Show(history, settings);
        Motion.AnimationsEnabled = () => false;
        try
        {
            Assert.True(second.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.False(second.IsVisible);
        }
        finally
        {
            Motion.AnimationsEnabled = previousAnimations;
            second.Close();
        }
    }

    private static T CreatePrivate<T>(params object[] args) where T : Window =>
        (T)Activator.CreateInstance(
            typeof(T),
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null)!;

    private static T CreatePrivateForm<T>(params object[] args) where T : WF.Form =>
        (T)Activator.CreateInstance(
            typeof(T),
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null)!;

    /// <summary>
    /// Drives the private ReorderSelected for all four z-order moves and an undo round-trip,
    /// asserting AnnotationCanvas.Children ends in the right order each time. The Insert-after-Remove
    /// index math is the easy place to get an off-by-one, so it gets a real check.
    /// </summary>
    /// <summary>
    /// The tool row must not move when the active tool changes. Format controls appear and
    /// collapse per tool on the right of the same row; if the tool row's position depends on
    /// that cluster's width, every switch slides the buttons out from under the cursor.
    /// </summary>
    private static void ToolRowStabilitySmoke(EditorWindow editor)
    {
        var toolPanel = (FrameworkElement)editor.FindName("ToolPanel")!;

        double Left()
        {
            editor.UpdateLayout();
            return toolPanel.TransformToAncestor(editor).Transform(new Point(0, 0)).X;
        }

        double baseline = Left();

        // Tools whose format cluster differs the most: shapes carry fill and stroke, the
        // redaction carries style + strength and no colour, the magnifier carries a zoom,
        // pan carries nothing at all. (Survey's separate Pixelate/Blur tools are gone —
        // they are two of the four styles of the one re-editable redaction mark.)
        foreach (string name in new[]
        {
            "SelectToolBtn", "PanToolBtn", "RectangleToolBtn", "RedactToolBtn",
            "SpotlightToolBtn", "MagnifierToolBtn", "CropToolBtn", "EyedropperToolBtn",
        })
        {
            if (editor.FindName(name) is not RadioButton rb) continue;
            rb.IsChecked = true;
            double now = Left();
            Assert.True(Math.Abs(now - baseline) < 0.5,
                $"tool row moved {now - baseline:0.##}px when selecting {name}");
        }

        // The group buttons swap the sub-strip, the other thing that shoved the row over.
        // Five chips now, not Survey's four: Image joined them.
        foreach (string name in new[]
        {
            "DrawGroupBtn", "ShapeGroupBtn", "TextGroupBtn", "EffectsGroupBtn", "ImageGroupBtn",
        })
        {
            if (editor.FindName(name) is not Button b) continue;
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            double now = Left();
            Assert.True(Math.Abs(now - baseline) < 0.5,
                $"tool row moved {now - baseline:0.##}px when opening {name}");
        }

        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;
    }

    /// <summary>
    /// Every annotation that can be selected can be rotated — including the counter, which
    /// carried no handles at all, and the callout, whose own knee handles must not displace
    /// the grip. The grip is the Ellipse in the handle layer.
    /// </summary>
    private static void RotationGripSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        var handleLayer = (Canvas)editor.FindName("HandleLayer")!;
        var selField = type.GetField("_selected", NP)!;
        var updateVisual = type.GetMethod("UpdateSelectionVisual", NP)!;

        bool GripVisible() => handleLayer.Children.OfType<System.Windows.Shapes.Ellipse>()
            .Any(e => e.Visibility == Visibility.Visible);

        void SelectAndLayout(FrameworkElement el)
        {
            selField.SetValue(editor, el);
            updateVisual.Invoke(editor, null);
            editor.UpdateLayout();
        }

        canvas.Children.Clear();

        // A plain box annotation — the baseline that always had handles.
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = 90,
            Height = 60,
            Tag = new AnnotationData { Type = AnnotationData.TypeRectangle },
        };
        Canvas.SetLeft(rect, 20);
        Canvas.SetTop(rect, 20);
        canvas.Children.Add(rect);
        rect.Measure(new Size(90, 60));
        rect.Arrange(new Rect(0, 0, 90, 60));
        SelectAndLayout(rect);
        Assert.True(GripVisible(), "rectangle should carry a rotation grip");

        // The counter: previously fell through to HideHandles, so it could not be rotated.
        var badge = AnnotationFactory.CreateStepBadge(1, Colors.Red, 4, letters: false);
        badge.Tag = new AnnotationData { Type = AnnotationData.TypeStep };
        Canvas.SetLeft(badge, 140);
        Canvas.SetTop(badge, 40);
        canvas.Children.Add(badge);
        badge.Measure(new Size(200, 200));
        badge.Arrange(new Rect(0, 0, badge.DesiredSize.Width, badge.DesiredSize.Height));
        SelectAndLayout(badge);
        Assert.True(GripVisible(), "counter should carry a rotation grip");

        // Rotating it must actually take, and survive a round-trip through the model.
        AnnotationTransform.SetAngle(badge, 37);
        Assert.Equal(37, AnnotationTransform.AngleOf(badge), 3);
        Assert.Equal(new Vector(0, 0), AnnotationTransform.OffsetOf(badge));

        // Moving a rotated mark must not straighten it.
        AnnotationTransform.SetOffset(badge, new Vector(12, 8));
        Assert.Equal(37, AnnotationTransform.AngleOf(badge), 3);
        Assert.Equal(new Vector(12, 8), AnnotationTransform.OffsetOf(badge));

        canvas.Children.Clear();
        selField.SetValue(editor, null);
        updateVisual.Invoke(editor, null);
    }

    private static void ZOrderReorderSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (System.Windows.Controls.Canvas)editor.FindName("AnnotationCanvas")!;
        var selField = type.GetField("_selected", NP)!;
        var zmoveType = type.GetNestedType("ZMove", System.Reflection.BindingFlags.NonPublic)!;
        var reorder = type.GetMethod("ReorderSelected", NP)!;
        var undoAsync = type.GetMethod("UndoAsync", NP)!;

        (System.Windows.UIElement a, System.Windows.UIElement b, System.Windows.UIElement c) Setup()
        {
            canvas.Children.Clear();
            var a = new System.Windows.Shapes.Rectangle();
            var b = new System.Windows.Shapes.Rectangle();
            var c = new System.Windows.Shapes.Rectangle();
            canvas.Children.Add(a); canvas.Children.Add(b); canvas.Children.Add(c);
            return (a, b, c);
        }
        void Move(System.Windows.UIElement sel, string move)
        {
            selField.SetValue(editor, sel);
            reorder.Invoke(editor, new[] { Enum.Parse(zmoveType, move) });
        }
        System.Windows.UIElement[] Order() => canvas.Children.Cast<System.Windows.UIElement>().ToArray();

        var (a, b, c) = Setup(); Move(b, "Front");    Assert.Equal(new[] { a, c, b }, Order());
        (a, b, c) = Setup();     Move(b, "Back");     Assert.Equal(new[] { b, a, c }, Order());
        (a, b, c) = Setup();     Move(a, "Forward");  Assert.Equal(new[] { b, a, c }, Order());
        (a, b, c) = Setup();     Move(c, "Backward"); Assert.Equal(new[] { a, c, b }, Order());

        // Undo restores original order.
        (a, b, c) = Setup(); Move(b, "Front");
        ((Task)undoAsync.Invoke(editor, null)!).GetAwaiter().GetResult();
        Assert.Equal(new[] { a, b, c }, Order());

        canvas.Children.Clear();
    }

    private static void EditorShellContractSmoke(EditorWindow editor)
    {
        // Nothing hides behind an overflow popup: the sweep retired its own "More tools"
        // flyout and the merge retired Survey's "more chrome" one. Everything they held has
        // a visible home — rotate / flip / image size on the crop bar, and add image, add
        // capture, add background, extract text and export GIF in the Image group's strip.
        Assert.Null(editor.FindName("MoreToolsButton"));
        Assert.Null(editor.FindName("MoreToolsPopup"));
        Assert.Null(editor.FindName("MoreToolPanel"));
        Assert.Null(editor.FindName("MoreChromeBtn"));
        Assert.Null(editor.FindName("MoreChromePopup"));
        Assert.Null(editor.FindName("EmojiToolBtn"));
        Assert.Null(editor.FindName("DragMeHandle"));
        Assert.Empty(EditorShellContract.MoreToolOrder);

        // Survey's two separate effect tools are gone: Pixelate and Blur are two of the
        // four STYLES of the one re-editable redaction mark, so there is one strength
        // control on the strip instead of an S/M/L combo per tool.
        Assert.Null(editor.FindName("PixelateToolBtn"));
        Assert.Null(editor.FindName("BlurToolBtn"));
        Assert.Null(editor.FindName("EffectStrengthPanel"));
        Assert.Null(editor.FindName("EffectStrengthBox"));
        Assert.Null(editor.FindName("EffectStrengthLabel"));
        // Rectangle + Fill covers what the second rectangle button used to.
        Assert.Null(editor.FindName("FilledRectangleToolBtn"));

        // Pan/Select hang off the centred cluster in their own panel (Survey's
        // `position:absolute; right:100%` block, AppShell.jsx:1468-1477); the primary order
        // is the two panels read left to right. WinShot adds a FIFTH chip, Image, for the
        // one-shot things that act on the picture rather than adding a mark.
        var panSelect = (StackPanel)editor.FindName("PanSelectPanel")!;
        var primary = (StackPanel)editor.FindName("ToolPanel")!;
        string[] primaryOrder = panSelect.Children.OfType<FrameworkElement>()
            .Concat(primary.Children.OfType<FrameworkElement>())
            .Where(el => el.Visibility == Visibility.Visible)
            .Select(el => el.Tag as string)
            .Where(tag => !string.IsNullOrEmpty(tag))
            .Select(tag => tag!)
            .ToArray();
        Assert.Equal(EditorShellContract.PrimaryToolOrder, primaryOrder);

        // Each category's 34px strip holds exactly the tools the contract lists, in order.
        foreach (EditorToolCategory category in EditorShellContract.CategoryOrder)
        {
            var row = (StackPanel)editor.FindName(GroupPanelName(category))!;
            Assert.Equal(EditorShellContract.ToolsIn(category), GroupToolTags(row));
        }

        string[] namedControls =
        {
            "BtnUndo", "BtnRedo", "SelectToolBtn", "PanToolBtn",
            "DrawGroupBtn", "ShapeGroupBtn", "TextGroupBtn", "EffectsGroupBtn", "ImageGroupBtn",
            "RectangleToolBtn", "EllipseToolBtn", "LineToolBtn", "ArrowToolBtn",
            "TextToolBtn", "CalloutToolBtn",
            "RedactToolBtn", "SpotlightToolBtn", "MagnifierToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EraserToolBtn",
            "EyedropperToolBtn",
            "CropUtilityBtn", "CropToolBtn", "AddImageBtn", "AddCaptureBtn", "AddBackgroundBtn",
            "OcrRegionBtn", "ExportGifBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "SnapToEdgesCheck", "RedactTextOnlyCheck", "SketchCheck", "MagnifySlider",
            "ColorWellBtn", "ArrowStyleBox", "LineStyleBox", "CropRatioBox", "RedactStyleBox",
            "StepModeBox", "SaveColorBtn",
            "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnSave", "BtnDone", "BtnPin", "BtnCopy", "BtnCenter",
            "BtnZoomIn", "BtnZoomOut", "FitMenuBtn", "FitPageItem", "FitWidthItem", "FitHeightItem",
        };
        foreach (string name in namedControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), name);
        }

        // Every control that shows only a glyph has to say what it does out loud.
        string[] iconOnlyControls =
        {
            "BtnUndo", "BtnRedo", "SelectToolBtn", "PanToolBtn",
            "DrawGroupBtn", "ShapeGroupBtn", "TextGroupBtn", "EffectsGroupBtn", "ImageGroupBtn",
            "RectangleToolBtn", "EllipseToolBtn", "LineToolBtn", "ArrowToolBtn",
            "TextToolBtn", "CalloutToolBtn",
            "RedactToolBtn", "SpotlightToolBtn", "MagnifierToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EraserToolBtn",
            "EyedropperToolBtn",
            "CropUtilityBtn", "CropToolBtn", "AddImageBtn", "AddCaptureBtn", "AddBackgroundBtn",
            "OcrRegionBtn", "ExportGifBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "SnapToEdgesCheck", "RedactTextOnlyCheck", "SketchCheck",
            "ColorWellBtn", "RedactStyleBox", "StepModeBox", "SaveColorBtn",
            "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnPin", "BtnCopy", "BtnCenter", "BtnSave",
            "AlignMenuBtn", "FormatOverflowBtn", "BtnZoomIn", "BtnZoomOut", "FitMenuBtn",
        };
        foreach (string name in iconOnlyControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)), name);
        }

        var select = (RadioButton)editor.FindName("SelectToolBtn")!;
        var pan = (RadioButton)editor.FindName("PanToolBtn")!;
        var undo = (Button)editor.FindName("BtnUndo")!;
        var copy = (Button)editor.FindName("BtnCopy")!;
        foreach (Control control in new Control[] { select, pan, undo, copy })
        {
            control.ApplyTemplate();
            Assert.NotNull(control.Template.FindName("FocusRing", control));
            Assert.True(control.Focusable);
            Assert.True(control.IsTabStop);
        }

        Assert.Null(editor.FindName("EditorStyleBar"));
        var groupBar = (Border)editor.FindName("EditorGroupBar")!;
        // Survey PORTALS the 34px sub-row in only while a category is open
        // (PDFViewer.jsx:32015) and OVERLAYS it on the canvas (AppShell.jsx:2727-2740).
        var groupHost = (FrameworkElement)editor.FindName("EditorGroupToolsHost")!;
        var groupTools = (StackPanel)editor.FindName("EditorGroupTools")!;
        var colorBar = (StackPanel)editor.FindName("EditorColorBar")!;
        var formatBar = (StackPanel)editor.FindName("EditorFormatBar")!;
        var color = (StackPanel)editor.FindName("ColorPanel")!;
        var thickness = (StackPanel)editor.FindName("ThicknessPanel")!;
        var fill = (StackPanel)editor.FindName("FillPanel")!;
        var arrowStyle = (StackPanel)editor.FindName("ArrowStylePanel")!;
        var redactStyle = (StackPanel)editor.FindName("RedactStylePanel")!;
        var redactStrength = (StackPanel)editor.FindName("RedactStrengthPanel")!;
        var magnify = (StackPanel)editor.FindName("MagnifyPanel")!;
        var cropRatio = (StackPanel)editor.FindName("CropRatioPanel")!;
        var cropTools = (StackPanel)editor.FindName("CropToolsPanel")!;
        // Survey's size control is an editable field plus a preset popover, not a combo
        // (components/AnnotationSizeControl.jsx); it replaced the sweep's SizeBox pair.
        var sizeInput = (TextBox)editor.FindName("SizeInput")!;
        var sizeField = (Border)editor.FindName("SizeField")!;
        var sizeHeading = (TextBlock)editor.FindName("SizePresetsHeading")!;
        var sizePresets = (StackPanel)editor.FindName("SizePresetList")!;
        var viewport = (Grid)editor.FindName("Viewport")!;
        var topToolbar = (Border)editor.FindName("TopToolbar")!;
        var secondary = (FrameworkElement)editor.FindName("SecondaryToolPanel")!;
        // Survey's zoom rail replaced the sweep's label + menu-button pair, and Survey's
        // size field replaced the sweep's combo.
        var zoomBox = (TextBox)editor.FindName("ZoomBox")!;
        Assert.Null(editor.FindName("ZoomMenuButton"));
        Assert.Null(editor.FindName("ZoomLabel"));
        Assert.Null(editor.FindName("SizeBox"));
        Assert.Null(editor.FindName("SizeFieldBorder"));

        // Survey centres the tool cluster and hangs undo/redo and the props off it as
        // absolute side blocks. Equal star side columns give the same geometry, so the
        // tools stay put on every tool switch. ToolRowStabilitySmoke pins this.
        Assert.Equal(HorizontalAlignment.Center, primary.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Center, groupTools.HorizontalAlignment);
        Assert.True(groupHost.MinWidth < 40,
            "Group tools must size to the visible chips, not a reserved blank slot.");
        Assert.Equal(HorizontalAlignment.Stretch, groupBar.HorizontalAlignment);
        // Survey's two chrome tokens have fixed roles: #181c24 for every band that SPANS the
        // viewer width (top bar AppShell.jsx:1343, sub-row PDFViewer.jsx:32021) and #12151c
        // for the vertical rail/panel surface only (AppShell.jsx:2697/:2847,
        // SurveySpacesRail.jsx:1229). The bottom band spans the width, so it shares the top
        // bar's material — that is what makes the chrome read as one system.
        var bottomBar = (Border)editor.FindName("EditorBottomBar")!;
        var rail = (Border)editor.FindName("ZoomRail")!;
        Assert.Equal(topToolbar.Background, bottomBar.Background);
        Assert.Equal(groupBar.Background, bottomBar.Background);
        Assert.NotEqual(rail.Background, bottomBar.Background);
        // Survey's Pan/Select block (AppShell.jsx:1468-1477/:1631): Select edge -> divider =
        // gap 6 + divider margin 4 = 10px; divider -> first chip = divider margin 4 +
        // block paddingRight 8 = 12px.
        var divider = panSelect.Children.OfType<System.Windows.Shapes.Rectangle>().Last();
        Assert.Equal(10d, divider.Margin.Left + select.Margin.Right);
        Assert.Equal(12d, divider.Margin.Right + panSelect.Margin.Right
            + ((Button)editor.FindName("DrawGroupBtn")!).Margin.Left);
        Assert.True(IsLogicalDescendant(topToolbar, formatBar),
            "Format chrome must sit on the primary row next to color and size, like Survey.");
        Assert.False(IsLogicalDescendant(topToolbar, groupBar),
            "Group tools sit in a chrome strip over the canvas so they do not shrink the image.");
        Assert.True(IsLogicalDescendant(topToolbar, undo), "Undo must live on the top toolbar, not over the image.");
        Assert.True(IsLogicalDescendant(topToolbar, (DependencyObject)editor.FindName("RotateCcwBtn")!),
            "Rotate must live next to Crop, not over the image.");
        Assert.DoesNotContain(((Grid)viewport.Parent).Children.OfType<FrameworkElement>(),
            el => el.Name is "EditorStyleBar");

        // The pointer tool has no controls of its own, so the sub-row disappears entirely.
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, groupHost.Visibility);
        // No category open -> no band and no hairline at all: the top bar butts straight
        // onto the canvas, which is why both are #181c24 in Survey.
        Assert.Equal(Visibility.Collapsed, groupBar.Visibility);
        Assert.Equal(34d, groupBar.Height);
        Assert.Equal(VerticalAlignment.Top, groupBar.VerticalAlignment);
        Assert.DoesNotContain(primary.Children.OfType<FrameworkElement>(),
            el => el.Tag as string == "Step");
        Assert.Contains(((StackPanel)editor.FindName("ShapeGroupPanel")!).Children.OfType<RadioButton>(),
            rb => rb.Tag as string == "Step");
        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        ((RadioButton)editor.FindName("PanToolBtn")!).IsChecked = true;
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        PackEditorFormat(editor);
        double viewportHSelect = viewport.ActualHeight;
        Assert.Equal(Visibility.Collapsed, color.Visibility);
        Assert.Equal(Visibility.Collapsed, thickness.Visibility);

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Equal(Visibility.Visible, groupHost.Visibility);
        Assert.Equal(Visibility.Visible, groupBar.Visibility);
        // AppShell.jsx:1637-1641: the chip is a TOGGLE — clicking the ACTIVE category
        // closes the sub-row and leaves the tool alone; the derive-from-tool effect
        // (PDFViewer.jsx:4535-4555) does not re-open it because the tool never changed.
        var shapeChip = (Button)editor.FindName("ShapeGroupBtn")!;
        shapeChip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        editor.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, groupBar.Visibility);
        Assert.True(((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked);
        Assert.True(Math.Abs(viewport.ActualHeight - viewportHSelect) < 0.5,
            "Opening or closing the sub-row must not resize the canvas — it overlays it.");
        shapeChip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Equal(Visibility.Visible, groupBar.Visibility);
        // Survey's sub-row centres on the WINDOW, not on the canvas column: the strip is
        // justifyContent:'center' (PDFViewer.jsx:32026) inside a centre column that is
        // symmetric because BOTH rails are 48px (AppShell.jsx:2692/:2841), so it lands on
        // the same x as the top bar's centred cluster (AppShell.jsx:1344) and the sub-row
        // tools sit under the chip that opened them. WinShot has only the right rail, so
        // the sub-row host carries a 48px left margin to restore that symmetry — which
        // means the strip is NOT centred in its own bar; it is centred on the chips.
        double arrowToolsW = groupTools.ActualWidth;
        double arrowToolsCentre = groupTools.TranslatePoint(new Point(0, 0), editor).X
            + arrowToolsW / 2;
        double chipsCentre = primary.TranslatePoint(new Point(0, 0), editor).X
            + primary.ActualWidth / 2;
        Assert.True(Math.Abs(arrowToolsCentre - chipsCentre) < 1,
            $"Shape sub-row is not under the chips that opened it: sub-row={arrowToolsCentre:0.##} chips={chipsCentre:0.##}");
        Assert.True(Math.Abs(viewport.ActualHeight - viewportHSelect) < 0.5,
            $"Viewport shrank Arrow vs Select: {viewportHSelect:0.##} → {viewport.ActualHeight:0.##}");
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("ShapeGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Visible, colorBar.Visibility);
        Assert.Equal(Visibility.Visible, formatBar.Visibility);
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Visible, arrowStyle.Visibility);
        Assert.Equal(Visibility.Collapsed, fill.Visibility);
        // Arming a tool lights its group chip and swaps the 34px strip to that group's row.
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("DrawGroupPanel")!).Visibility);
        Assert.Equal("WIDTH", sizeHeading.Text);

        Assert.Null(editor.FindName("EraserPartialBtn"));
        // Survey keeps eraser mode in the tool's OWN split menu inside the Draw sub-row
        // (PDFViewer.jsx:32095-32135), not in a top-row combo.
        Assert.Null(editor.FindName("EraserModeBox"));
        Assert.NotNull(editor.FindName("EraserModePopup"));
        Assert.NotNull(editor.FindName("EraserCaretBtn"));
        typeof(EditorWindow).GetMethod("FitEditorCombos",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, null);
        var lineBox = (ComboBox)editor.FindName("LineStyleBox")!;
        var arrowBox = (ComboBox)editor.FindName("ArrowStyleBox")!;
        Assert.True(lineBox.Width > 40, $"Line style combo was not sized to content: {lineBox.Width}");
        double lineWidth = lineBox.Width;
        foreach (ComboBoxItem item in lineBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == "dashed") { lineBox.SelectedItem = item; break; }
        editor.UpdateLayout();
        Assert.Equal(lineWidth, lineBox.Width);
        double arrowWidth = arrowBox.Width;
        foreach (ComboBoxItem item in arrowBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == "none") { arrowBox.SelectedItem = item; break; }
        editor.UpdateLayout();
        Assert.Equal(arrowWidth, arrowBox.Width);

        // The size field takes any whole number, not just the old 2 / 4 / 6, and the preset
        // popover offers Survey's twelve widths, each row carrying its value.
        sizeInput.Text = "17";
        Assert.Equal("17", sizeInput.Text);
        Assert.Equal(
            AnnotationSize.Presets,
            sizePresets.Children.OfType<Button>().Select(b => (int)(double)b.Tag!).ToArray());

        // Out-of-range input is clamped to the stored value rather than rejected. The box
        // keeps the raw text while the user is mid-type and normalises on commit, so the
        // clamp shows up the next time the field is re-synced.
        sizeInput.Text = "999";
        ((RadioButton)editor.FindName("LineToolBtn")!).IsChecked = true;
        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal("50", sizeInput.Text);
        sizeInput.Text = "7";

        // Each tool keeps its own size: Text is independent of Arrow. The word "Width" /
        // "Size" left the toolbar with the other labels and lives on the preset popover.
        ((RadioButton)editor.FindName("TextToolBtn")!).IsChecked = true;
        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Equal("4", sizeInput.Text);
        var primaryRow = (Grid)editor.FindName("ToolbarPrimaryRow")!;
        Assert.True(primaryRow.ActualHeight <= 48,
            $"Primary row grew instead of staying one line: {primaryRow.ActualHeight}");
        Assert.True(topToolbar.ActualHeight <= 56,
            $"Top chrome grew extra rows with Text selected: {topToolbar.ActualHeight}");
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("TextGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("ShapeGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Collapsed, ((TextBlock)editor.FindName("ThicknessLabel")!).Visibility);
        // Survey's Width control for text is a STROKE width (AppShell.jsx:2464-2482), so the
        // shared size box keeps the stroke presets; the font size lives in FontSizeBox, which
        // — like Survey's rich-text row — is only up while an editor is mounted.
        Assert.Contains(sizePresets.Children.OfType<Button>(), b => (double?)b.Tag == 16);
        Assert.DoesNotContain(sizePresets.Children.OfType<Button>(), b => (double?)b.Tag == 72);
        Assert.False(string.IsNullOrEmpty(sizeInput.Text));
        var fontSizeBox = (ComboBox)editor.FindName("FontSizeBox")!;
        Assert.Equal(Visibility.Collapsed, fontSizeBox.Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("TextStylePanel")!).Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("LineStylePanel")!).Visibility);
        // Same rule as the Shape sub-row above: centred on the top bar's chips, not on the bar.
        double textToolsW = groupTools.ActualWidth;
        double textToolsCentre = groupTools.TranslatePoint(new Point(0, 0), editor).X + textToolsW / 2;
        Assert.True(Math.Abs(textToolsCentre - chipsCentre) < 1,
            $"Text sub-row is not under the chips that opened it: sub-row={textToolsCentre:0.##} chips={chipsCentre:0.##}");
        Assert.True(textToolsW < arrowToolsW - 8,
            $"Text group still occupies the Shapes-width blank slot: text={textToolsW:0} shapes={arrowToolsW:0}");
        var well = (FrameworkElement)editor.FindName("ColorWellBtn")!;
        Point wellOrigin = well.TranslatePoint(new Point(0, 0), editor);
        Point sizeOrigin = sizeField.TranslatePoint(new Point(0, 0), editor);
        Assert.True(sizeOrigin.X - (wellOrigin.X + well.ActualWidth) < 16,
            $"Color well and size field have a gap: wellRight={wellOrigin.X + well.ActualWidth:0} size={sizeOrigin.X:0}");
        Assert.True(Math.Abs(viewport.ActualHeight - viewportHSelect) < 0.5,
            $"Viewport shrank Text vs Select: {viewportHSelect:0.##} → {viewport.ActualHeight:0.##}");
        var overflowBtn = (Button)editor.FindName("FormatOverflowBtn")!;
        var overflowPanel = (StackPanel)editor.FindName("FormatOverflowPanel")!;
        var bold = (Button)editor.FindName("BoldBtn")!;
        var textButtons = (FrameworkElement)editor.FindName("TextFormatButtons")!;
        // Survey's rich-text row is only portalled in while an editor is mounted, so with the
        // Text tool merely armed there is nothing on the strip left to overflow.
        Assert.Equal(Visibility.Collapsed, overflowBtn.Visibility);
        Assert.DoesNotContain(overflowPanel.Children.OfType<FrameworkElement>(),
            el => el.Name is "TextFormatButtons" or "BoldBtn" or "ItalicBtn");
        Assert.True(IsLogicalDescendant(formatBar, textButtons),
            "B/I/U/S must stay on the primary strip, not the overflow flyout.");
        Assert.True(IsLogicalDescendant(formatBar, bold));
        Point textSave = BarRightEdge(editor);
        Point textSec = secondary.TranslatePoint(new Point(0, 0), editor);
        Assert.True(textSec.X + secondary.ActualWidth <= textSave.X + 0.5,
            $"Text secondary runs off the top bar: sec={textSec.X}+{secondary.ActualWidth} save={textSave.X}");

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal("7", sizeInput.Text);

        ((RadioButton)editor.FindName("CalloutToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Contains(sizePresets.Children.OfType<Button>(), b => (double?)b.Tag == 1);
        Assert.Contains(sizePresets.Children.OfType<Button>(), b => (double?)b.Tag == 50);
        Assert.DoesNotContain(sizePresets.Children.OfType<Button>(), b => (double?)b.Tag == 72);
        // Same rule for callouts: Survey's font-size dropdown belongs to the rich-text row,
        // which only exists while an editor is mounted (TextEditOverlay.jsx:278-318).
        Assert.Equal(Visibility.Collapsed, fontSizeBox.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Point strokeOrigin = thickness.TranslatePoint(new Point(0, 0), editor);
        Point saveOrigin = BarRightEdge(editor);
        Assert.True(strokeOrigin.X + thickness.ActualWidth <= saveOrigin.X + 0.5,
            $"Callout size runs off the top bar: size={strokeOrigin.X}+{thickness.ActualWidth} save={saveOrigin.X}");
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Point formatOrigin = formatBar.TranslatePoint(new Point(0, 0), editor);
        saveOrigin = BarRightEdge(editor);
        Assert.True(formatOrigin.X + formatBar.ActualWidth <= saveOrigin.X + 0.5,
            $"Format extras sit on Save as…: format={formatOrigin.X}+{formatBar.ActualWidth} save={saveOrigin.X}");
        Point calloutSec = secondary.TranslatePoint(new Point(0, 0), editor);
        Assert.True(calloutSec.X + secondary.ActualWidth <= saveOrigin.X + 0.5,
            $"Callout secondary runs off the top bar: sec={calloutSec.X}+{secondary.ActualWidth} save={saveOrigin.X}");
        var done = (Button)editor.FindName("BtnDone")!;
        Point doneOrigin = done.TranslatePoint(new Point(0, 0), editor);
        Assert.True(doneOrigin.X + done.ActualWidth <= editor.ActualWidth + 0.5,
            $"Done overflows the window: done={doneOrigin.X}+{done.ActualWidth} win={editor.ActualWidth}");
        Assert.Equal(Visibility.Visible, overflowBtn.Visibility);
        Assert.DoesNotContain(overflowPanel.Children.OfType<FrameworkElement>(),
            el => el.Name is "TextFormatButtons" or "BoldBtn");
        Assert.True(IsLogicalDescendant(formatBar, textButtons));
        Assert.NotNull(editor.FindName("BoldBtn"));
        Assert.NotNull(editor.FindName("FontFamilyBox"));
        Assert.NotNull(editor.FindName("AlignMenuBtn"));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName((Button)editor.FindName("BoldBtn")!)));
        var italicGlyph = Assert.IsType<TextBlock>(((Button)editor.FindName("ItalicBtn")!).Content);
        Assert.Equal(FontStyles.Italic, italicGlyph.FontStyle);
        Assert.False(italicGlyph.RenderTransform is SkewTransform);

        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        var label = AnnotationFactory.CreateStyledTextLabel("Hello", Brushes.White, 16, TextStyle.Plain);
        label.Tag = AnnotationData.ForText(new Point(10, 10), "Hello", TextStyle.Plain, 16, Colors.White);
        canvas.Children.Add(label);
        typeof(EditorWindow).GetMethod("Select",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { label });
        editor.UpdateLayout();
        // Survey shows the rich-text row solely while an editor is mounted, NOT when a text
        // annotation is merely selected (AppShell.jsx:1756 gates on bottomToolbarApi.richTextEditor).
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("TextStylePanel")!).Visibility);
        Assert.Equal(Visibility.Visible, color.Visibility);
        // The 34px category strip follows the ARMED tool only: `activeCategoryDropdown` starts
        // null and is force-cleared whenever activeTool becomes pan/select/text-select
        // (PDFViewer.jsx:4340, :4558-4568) — selecting a committed mark never re-opens it. The
        // format chrome above still follows the selection (contextTool), hence color stays up.
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("TextGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Collapsed, groupBar.Visibility);
        // No category chip is gold under Select either (AppShell.jsx:1647/1666/1685/1719).
        Assert.Equal(
            ((SolidColorBrush)editor.FindResource("ChromeTextBrush")).Color,
            ((SolidColorBrush)((Button)editor.FindName("TextGroupBtn")!).Foreground).Color);
        canvas.Children.Clear();
        typeof(EditorWindow).GetMethod("Select",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object?[] { null });

        // One redaction tool with a style menu and a continuous strength — no S/M/L radios,
        // and no colour well, because a redaction has no colour to pick.
        ((RadioButton)editor.FindName("RedactToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Equal(Visibility.Visible, redactStyle.Visibility);
        Assert.Equal(Visibility.Visible, redactStrength.Visibility);
        Assert.Equal(Visibility.Collapsed, color.Visibility);
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("EffectsGroupPanel")!).Visibility);
        var redactBox = (ComboBox)editor.FindName("RedactStyleBox")!;
        Point redactOrigin = redactBox.TranslatePoint(new Point(0, 0), editor);
        Point redactSave = BarRightEdge(editor);
        Assert.True(redactOrigin.X + redactBox.ActualWidth <= redactSave.X + 0.5,
            $"Redact style runs off the top bar: style={redactOrigin.X}+{redactBox.ActualWidth} save={redactSave.X}");

        // "Text only" belongs to the STYLE group, not the strength group. Inside the
        // strength group it vanished for Black Out — the one style whose text-only form
        // Shottr's row exists for — while the flag stayed live and every drag silently
        // produced one mark per word with no control on screen to say so.
        var textOnly = (CheckBox)editor.FindName("RedactTextOnlyCheck")!;
        Assert.Same(redactStyle, textOnly.Parent);
        var applyRedactStyle = typeof(EditorWindow).GetMethod("ApplyRedactStyle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        applyRedactStyle.Invoke(editor, new object[] { RedactStyle.BlackOut });
        editor.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, redactStrength.Visibility);
        Assert.Equal(Visibility.Visible, redactStyle.Visibility);
        Assert.Equal(Visibility.Visible, textOnly.Visibility);
        applyRedactStyle.Invoke(editor, new object[] { RedactStyle.Pixelate });
        editor.UpdateLayout();
        Assert.Equal(Visibility.Visible, redactStrength.Visibility);

        // The magnifier is the third Effects mark and carries its own zoom, not a strength.
        ((RadioButton)editor.FindName("MagnifierToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        Assert.Equal(Visibility.Visible, magnify.Visibility);
        Assert.Equal(Visibility.Collapsed, redactStrength.Visibility);

        typeof(EditorWindow).GetMethod("OnCropClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { editor, new RoutedEventArgs() });
        editor.UpdateLayout();
        PackEditorFormat(editor);
        Assert.Equal(Visibility.Visible, cropRatio.Visibility);
        // Rotate / flip / image size / snap came out of Survey's "more" popup and live on
        // the crop bar now.
        Assert.Equal(Visibility.Visible, cropTools.Visibility);
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("RotateCcwBtn")!));
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("FlipHorizontalBtn")!));
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("FlipVerticalBtn")!));
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("ResizeImageBtn")!));
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("SnapToEdgesCheck")!));
        Assert.True(IsLogicalDescendant(cropTools, (DependencyObject)editor.FindName("ImageSizeLabel")!));
        var cropPanel = (FrameworkElement)editor.FindName("CropPanel")!;
        cropPanel.Visibility = Visibility.Visible;
        PackEditorFormat(editor);
        Button applyCrop = LogicalTreeHelper.GetChildren(cropPanel).OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == "Apply crop");
        Assert.True(applyCrop.IsVisible && applyCrop.Parent == cropPanel,
            "Apply crop must stay on the strip; overflow Don't crop / ratio first.");
        Point applyOrigin = applyCrop.TranslatePoint(new Point(0, 0), editor);
        Point cropSave = BarRightEdge(editor);
        Assert.True(applyOrigin.X + applyCrop.ActualWidth <= cropSave.X + 0.5,
            $"Apply crop runs off the top bar: apply={applyOrigin.X}+{applyCrop.ActualWidth} save={cropSave.X}");
        Assert.Contains(overflowPanel.Children.OfType<FrameworkElement>(),
            el => el.Name == "CropCancelBtn");
        Point toolsRight = primary.TranslatePoint(new Point(primary.ActualWidth, 0), editor);
        Assert.True(applyOrigin.X + 0.5 >= toolsRight.X - 8,
            $"Apply crop covers tools: apply={applyOrigin.X:0} toolsRight={toolsRight.X:0}");

        // A group chip opens its own strip and arms that strip's first tool; Image is the
        // one that arms nothing, so clicking it cannot drop the user into a crop.
        ((Button)editor.FindName("DrawGroupBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        editor.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("DrawGroupPanel")!).Visibility);
        Assert.True(((RadioButton)editor.FindName("FreehandToolBtn")!).IsChecked);
        ((Button)editor.FindName("ImageGroupBtn")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        editor.UpdateLayout();
        Assert.True(((RadioButton)editor.FindName("FreehandToolBtn")!).IsChecked);

        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;

        // The zoom readout is written in one place, on every view change — the old label +
        // combo pair went stale after a wheel zoom and never came back. The percentages
        // GLIDE now, so the tween is forced to land in one frame rather than raced.
        var animations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            typeof(EditorWindow).GetMethod("ZoomAt",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, new object[] { new Point(0, 0), 2.0 });
            Assert.Equal("200%", zoomBox.Text);
        }
        finally { Motion.AnimationsEnabled = animations; }
        Assert.False(string.IsNullOrWhiteSpace(
            AutomationProperties.GetHelpText((FrameworkElement)editor.FindName("FitMenuBtn")!)));

        // The one-row toolbar's 1152 DIP floor is not a promise a screen can keep — a
        // 1536x1024 laptop at 150 % is 1024 DIP of work area — and WM_GETMINMAXINFO would
        // enforce it anyway, opening the window wider than the monitor with the docked
        // Save as… / Done pair off the right edge and no way to drag narrower. Every open
        // path clamps, not just the grow: this editor came up the History/CLI way.
        var work = SystemParameters.WorkArea;
        Assert.True(editor.MinWidth <= work.Width + 0.5,
            $"MinWidth {editor.MinWidth} exceeds the {work.Width:0} DIP work area.");
        Assert.True(editor.MinHeight <= work.Height + 0.5,
            $"MinHeight {editor.MinHeight} exceeds the {work.Height:0} DIP work area.");
        Assert.True(editor.MinWidth > 0 && editor.MinHeight > 0);

        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        Assert.True(topToolbar.ActualWidth <= editor.ActualWidth);
        Assert.True(primary.ActualHeight <= 42, $"Primary tools wrapped at minimum width: {primary.ActualHeight}");
        ToolbarFitsAtMinimumWidth(editor);
        ContextRowNeverCoversTheFinishPair(editor);
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;

        MarqueeSelectsEveryIntersectingAnnotation(editor);
    }

    /// <summary>The 34px strip panel that carries a category's tools.</summary>
    private static string GroupPanelName(EditorToolCategory category) => category switch
    {
        EditorToolCategory.Draw => "DrawGroupPanel",
        EditorToolCategory.Shapes => "ShapeGroupPanel",
        EditorToolCategory.Text => "TextGroupPanel",
        EditorToolCategory.Redact => "EffectsGroupPanel",
        _ => "ImageGroupPanel",
    };

    /// <summary>
    /// The tool tags in a group's strip, in order. Walks the sub-tree rather than the
    /// panel's direct children because the eraser lives inside a Grid that also carries its
    /// mode caret — reading Children only would have silently dropped it from the contract.
    /// </summary>
    private static string[] GroupToolTags(DependencyObject root)
    {
        var tags = new List<string>();
        void Walk(DependencyObject node)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is RadioButton rb)
                {
                    if (rb.Tag is string tag && tag.Length > 0) tags.Add(tag);
                }
                else if (child is DependencyObject dep)
                {
                    Walk(dep);
                }
            }
        }
        Walk(root);
        return tags.ToArray();
    }

    private static void PackEditorFormat(EditorWindow editor)
    {
        var pack = typeof(EditorWindow).GetMethod("PackFormatOverflow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var frame = new DispatcherFrame();
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            pack.Invoke(editor, null);
            frame.Continue = false;
        });
        Dispatcher.PushFrame(frame);
        editor.UpdateLayout();
    }

    private static bool IsLogicalDescendant(DependencyObject root, DependencyObject child)
    {
        for (DependencyObject? d = child; d is not null; d = LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, root)) return true;
        return false;
    }

    private static void MarqueeSelectsEveryIntersectingAnnotation(EditorWindow editor)
    {
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        canvas.Children.Clear();
        // Both marks stay inside the 80x50 source: Survey clamps the marquee to the page
        // viewBox, so a band dragged past the image edge stops at the edge.
        var a = new System.Windows.Shapes.Rectangle { Width = 30, Height = 30, Fill = Brushes.Red };
        var b = new System.Windows.Shapes.Rectangle { Width = 20, Height = 20, Fill = Brushes.Blue };
        Canvas.SetLeft(a, 5); Canvas.SetTop(a, 5);
        Canvas.SetLeft(b, 50); Canvas.SetTop(b, 10);
        a.Tag = new AnnotationData { Type = AnnotationData.TypeRectangle };
        b.Tag = new AnnotationData { Type = AnnotationData.TypeRectangle };
        canvas.Children.Add(a);
        canvas.Children.Add(b);
        editor.UpdateLayout();

        typeof(EditorWindow).GetField("_marqueeStart",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(editor, new Point(0, 0));
        // Survey latches the band armed once the drag clears MIN_DRAG_PX; EndMarquee reads
        // that latch rather than re-measuring at release.
        typeof(EditorWindow).GetField("_marqueeArmed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(editor, true);
        typeof(EditorWindow).GetMethod("EndMarquee",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { new Point(140, 60) });

        var selection = (System.Collections.IList)typeof(EditorWindow).GetField("_selection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(editor)!;
        Assert.Equal(2, selection.Count);
        canvas.Children.Clear();
        typeof(EditorWindow).GetMethod("Select",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { null! });
    }

    /// <summary>
    /// The one row has to hold every tool's controls at the narrowest the window goes.
    /// Measuring the row's HEIGHT proves nothing — a control that does not fit is clipped in
    /// silence, which is how a whole crop group (Snap, then Image size) went missing without
    /// a red test. So this measures what the row WANTS, unconstrained, against the declared
    /// budget — and, when the display can actually hold a window that wide, against what the
    /// row is really given.
    ///
    /// The display check is not decoration: the minimum is 1152 DIP and a 1024 DIP screen
    /// cannot produce a 1152 DIP window, which is the whole reason the editor now clamps its
    /// minimum instead of forcing its Done button off the right edge. On such a display the
    /// arithmetic assertion is the contract; the clipping one has nothing to say.
    /// </summary>
    /// <summary>
    /// Below 1152 DIP the window's minimum is lowered to whatever the display holds, so the
    /// props block is given less than it needs, and its controls used to be arranged from
    /// x=0 outward straight across the Save as… / Done pair to its right. Two facts hold
    /// instead: the overflow is packed into the flyout rather than gone, and the finish pair
    /// is never painted over. (Survey's format packer replaced the sweep's ContextScroll, so
    /// the "is it scrollable" half of this is now "did anything actually get packed".)
    /// </summary>
    private static void ContextRowNeverCoversTheFinishPair(EditorWindow editor)
    {
        var secondary = (FrameworkElement)editor.FindName("SecondaryToolPanel")!;
        var overflowPanel = (StackPanel)editor.FindName("FormatOverflowPanel")!;
        var save = (Button)editor.FindName("BtnSave")!;
        var check = typeof(EditorWindow).GetMethod("CheckToolButton",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        double width = editor.Width;
        int overflowed = 0;
        try
        {
            // The narrowest real case: a 1920x1080 display at 200 % is 960 DIP.
            editor.MinWidth = 400;
            editor.Width = 700;
            foreach (EditorTool tool in Enum.GetValues<EditorTool>())
            {
                check.Invoke(editor, new object[] { tool });
                editor.UpdateLayout();
                PackEditorFormat(editor);
                if (secondary.Visibility != Visibility.Visible) continue;

                // The props block's painted extent stops before the finish pair. A block
                // that overran would put these the other way round.
                Point rowRight = secondary.TranslatePoint(new Point(secondary.ActualWidth, 0), editor);
                Point saveLeft = save.TranslatePoint(new Point(0, 0), editor);
                Assert.True(rowRight.X <= saveLeft.X + 0.5,
                    $"{tool}: the props block reaches {rowRight.X:0} DIP, past Save as… at {saveLeft.X:0}.");
                if (overflowPanel.Children.Count > 0) overflowed++;
            }

            // Otherwise the assertion above is true of a row that always fits, and the case
            // this method exists for was never reached.
            Assert.True(overflowed > 0, "no tool overflowed a 700 DIP row, so nothing was tested.");
        }
        finally
        {
            editor.MinWidth = EditorShellContract.MinimumEditorLogicalWidth;
            editor.Width = width;
            editor.UpdateLayout();
            PackEditorFormat(editor);
        }
    }

    private static void ToolbarFitsAtMinimumWidth(EditorWindow editor)
    {
        var primaryRow = (Grid)editor.FindName("ToolbarPrimaryRow")!;
        var check = typeof(EditorWindow).GetMethod("CheckToolButton",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            check.Invoke(editor, new object[] { tool });
            editor.UpdateLayout();

            primaryRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double needed = primaryRow.DesiredSize.Width;
            double available = primaryRow.ActualWidth;
            primaryRow.InvalidateMeasure();
            editor.UpdateLayout();

            Assert.True(needed <= EditorShellContract.PrimaryToolbarLogicalWidth,
                $"{tool}'s toolbar row needs {needed:0} DIP, over the declared " +
                $"{EditorShellContract.PrimaryToolbarLogicalWidth} DIP budget.");
            if (editor.ActualWidth >= EditorShellContract.MinimumEditorLogicalWidth - 0.5)
            {
                Assert.True(needed <= available + 0.5,
                    $"{tool}'s toolbar row is clipped at the minimum window width: " +
                    $"needs {needed:0} DIP, has {available:0}.");
            }
        }
    }

    /// <summary>
    /// A redaction is a live object: drawing one adds a canvas child and leaves the source
    /// bitmap byte-for-byte alone, restyling it is one more undoable step, and undo walks
    /// both back. The old destructive blur baked pixels and could only be undone by
    /// restoring a backup crop — that whole path is gone.
    /// </summary>
    private static void RedactionObjectSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        var source = (SD.Bitmap)type.GetField("_source", NP)!.GetValue(editor)!;
        var undoAsync = type.GetMethod("UndoAsync", NP)!;
        var redoAsync = type.GetMethod("RedoAsync", NP)!;

        // Undo/redo refuse to run while the source is still loading, so let the initial
        // image land first — otherwise every assertion below reads a no-op.
        WaitForEditorOperation(editor, type.GetField("_sourceOperationActive", NP)!);
        canvas.Children.Clear();
        SD.Color before = source.GetPixel(12, 12);
        var area = new Rect(4, 4, 40, 24);

        var mark = (System.Windows.Shapes.Rectangle)type.GetMethod("BeginRedaction", NP)!
            .Invoke(editor, new object[] { area })!;
        canvas.Children.Add(mark);
        type.GetMethod("UpdateRedactionPreview", NP)!.Invoke(editor, new object[] { mark, area, true });
        mark.Tag = AnnotationData.ForRedaction(area, RedactStyle.Pixelate, 45, seed: 7);
        type.GetMethod("PushAddElement", NP)!.Invoke(editor, new object[] { mark });

        Assert.Single(canvas.Children);
        // The whole point: nothing was baked into the picture.
        Assert.Equal(before.ToArgb(), source.GetPixel(12, 12).ToArgb());
        Assert.IsType<ImageBrush>(mark.Fill);

        // Restyling to Black Out is one undoable step and repaints flat black.
        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { mark });
        Assert.True((bool)type.GetMethod("RestyleSelectedRedaction", NP)!
            .Invoke(editor, new object?[] { RedactStyle.BlackOut, null, true })!);
        var blackedOut = Assert.IsType<SolidColorBrush>(mark.Fill);
        Assert.Equal(Colors.Black, blackedOut.Color);

        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.IsType<ImageBrush>(mark.Fill);
        WaitForTask((Task)redoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.IsType<SolidColorBrush>(mark.Fill);

        // Two more undos take the restyle and then the mark itself off the canvas; the
        // source is untouched at every step.
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Empty(canvas.Children);
        Assert.Equal(before.ToArgb(), source.GetPixel(12, 12).ToArgb());

        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { null });
        canvas.Children.Clear();
    }

    /// <summary>
    /// Arming the in-picker eyedropper and then picking another tool abandons the gesture,
    /// so the "come back to the picker" latch has to come down with it. Left armed, the next
    /// ordinary eyedropper sample reopened the picker on its own and wrote to whichever tab
    /// happened to be selected.
    /// </summary>
    private static void EyedropperPickerLatchSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var latch = type.GetField("_reopenPickerAfterSample", NP)!;
        var check = type.GetMethod("CheckToolButton", NP)!;

        type.GetMethod("OnPickerEyedropper", NP)!
            .Invoke(editor, new object?[] { null, new RoutedEventArgs() });
        Assert.True((bool)latch.GetValue(editor)!);

        check.Invoke(editor, new object?[] { EditorTool.Freehand });
        Assert.False((bool)latch.GetValue(editor)!);

        ((Popup)editor.FindName("ColorPickerPopup")!).IsOpen = false;
        check.Invoke(editor, new object?[] { EditorTool.Select });
    }

    /// <summary>
    /// Two magnification drags on one callout, then undo, undo, redo. The redo of the FIRST
    /// drag has to come back at that drag's value: while the gesture mutated the live
    /// AnnotationData in place, the first drag's undo entry and the second drag's shared one
    /// object, so redo replayed 6x where the user left 4x.
    /// </summary>
    private static void MagnifierZoomUndoSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        var undoAsync = type.GetMethod("UndoAsync", NP)!;
        var redoAsync = type.GetMethod("RedoAsync", NP)!;

        WaitForEditorOperation(editor, type.GetField("_sourceOperationActive", NP)!);
        canvas.Children.Clear();

        var area = new Rect(4, 4, 40, 24);
        var bubble = (System.Windows.Shapes.Rectangle)type.GetMethod("BeginMagnifier", NP)!
            .Invoke(editor, new object[] { area })!;
        canvas.Children.Add(bubble);
        bubble.Tag = AnnotationData.ForMagnifier(
            area, MagnifierLayout.DefaultFocus(area), MagnifierLayout.DefaultScale, Colors.White, 3);
        type.GetMethod("PushAddElement", NP)!.Invoke(editor, new object[] { bubble });
        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { bubble });

        var slider = (Slider)editor.FindName("MagnifySlider")!;
        var released = type.GetMethod("OnMagnifyReleased", NP)!;
        foreach (double zoom in new[] { 4d, 6d })
        {
            slider.Value = zoom;
            released.Invoke(editor, new object?[] { slider, new RoutedEventArgs() });
        }

        static double Zoom(FrameworkElement fe) => ((AnnotationData)fe.Tag!).Zoom ?? 0;

        Assert.Equal(6d, Zoom(bubble));
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Equal(4d, Zoom(bubble));
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Equal(MagnifierLayout.DefaultScale, Zoom(bubble));
        WaitForTask((Task)redoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Equal(4d, Zoom(bubble));

        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { null });
        canvas.Children.Clear();
    }

    /// <summary>
    /// Copy / paste / duplicate work on annotation OBJECTS, and each lands as one undoable
    /// step offset from the original so a copy is visibly a copy. The clipboard is static,
    /// which is what makes a mark copied in one editor window paste into another.
    /// </summary>
    private static void AnnotationClipboardSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
        var select = type.GetMethod("Select", NP)!;
        var copy = type.GetMethod("CopySelectedAnnotations", NP)!;
        var paste = type.GetMethod("PasteAnnotations", NP)!;
        var duplicate = type.GetMethod("DuplicateSelectedAnnotations", NP)!;
        var undoAsync = type.GetMethod("UndoAsync", NP)!;

        canvas.Children.Clear();
        var original = new System.Windows.Shapes.Rectangle
        {
            Width = 30,
            Height = 20,
            Stroke = Brushes.Red,
            StrokeThickness = 3,
            Tag = AnnotationData.ForShape(
                AnnotationData.TypeRectangle, new Rect(10, 10, 30, 20),
                Colors.Red, 3, ShapeFillMode.None),
        };
        Canvas.SetLeft(original, 10);
        Canvas.SetTop(original, 10);
        canvas.Children.Add(original);

        // Nothing selected: there is nothing to copy, and Ctrl+C falls through to the image.
        select.Invoke(editor, new object?[] { null });
        Assert.False((bool)copy.Invoke(editor, Array.Empty<object>())!);

        select.Invoke(editor, new object?[] { original });
        Assert.True((bool)copy.Invoke(editor, Array.Empty<object>())!);

        paste.Invoke(editor, Array.Empty<object>());
        Assert.Equal(2, canvas.Children.Count);
        var pasted = (FrameworkElement)canvas.Children[1];
        // Offset from the original, and carrying its own copy of the data.
        Assert.Equal(32d, AnnotationTransform.OffsetOf(pasted).X, 3);
        Assert.NotSame(original.Tag, pasted.Tag);

        duplicate.Invoke(editor, Array.Empty<object>());
        Assert.Equal(3, canvas.Children.Count);

        // Each is exactly one undo step.
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Equal(2, canvas.Children.Count);
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        Assert.Single(canvas.Children);

        select.Invoke(editor, new object?[] { null });
        canvas.Children.Clear();
    }

    /// <summary>
    /// Text is multi-line now, and a committed counter can be re-numbered by typing over it
    /// — with the rest of the sequence following, because renumbering one badge and leaving
    /// the counter where it was would just make the next one collide.
    /// </summary>
    private static readonly string LineBreak = ((char)10).ToString();

    private static void TextAndCounterSmoke(EditorWindow editor)
    {
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(EditorWindow);
        var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;

        // The editor box takes returns and wraps; a caption that runs long used to march
        // off the edge of the picture.
        var box = AnnotationFactory.CreateTextEditor(Brushes.White, 20);
        Assert.True(box.AcceptsReturn);
        Assert.Equal(TextWrapping.Wrap, box.TextWrapping);

        // One line reads the same centred or left; a paragraph does not.
        var oneLine = (TextBlock)AnnotationFactory.CreateStyledTextLabel(
            "Caption", Brushes.White, 20, TextStyle.Plain);
        Assert.Equal(TextAlignment.Center, oneLine.TextAlignment);
        var paragraph = (TextBlock)AnnotationFactory.CreateStyledTextLabel(
            string.Join(LineBreak, "one", "two", "three"), Brushes.White, 20, TextStyle.Plain);
        Assert.Equal(TextAlignment.Left, paragraph.TextAlignment);

        canvas.Children.Clear();
        var badge = AnnotationFactory.CreateStepBadge(1, Colors.Red, 4, letters: false);
        badge.Tag = AnnotationData.ForStep(new Point(5, 5), 1, Colors.Red, 4);
        canvas.Children.Add(badge);
        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { badge });

        var retype = type.GetMethod("TryRetypeStepNumber", NP)!;
        var nextStep = type.GetField("_nextStep", NP)!;
        Assert.True((bool)retype.Invoke(editor, new object[] { Key.D1 })!);
        Assert.True((bool)retype.Invoke(editor, new object[] { Key.D2 })!);

        string caption = badge.Children.OfType<TextBlock>().First().Text;
        Assert.Equal("12", caption);
        Assert.Equal(13, (int)nextStep.GetValue(editor)!);

        // The whole run is ONE undo entry, not one per digit.
        type.GetMethod("CommitStepRetype", NP)!.Invoke(editor, Array.Empty<object>());
        WaitForTask((Task)type.GetMethod("UndoAsync", NP)!.Invoke(editor, Array.Empty<object>())!);
        Assert.Equal("1", badge.Children.OfType<TextBlock>().First().Text);
        Assert.Equal(2, (int)nextStep.GetValue(editor)!);

        // A letter is not a digit, so it never reaches the badge.
        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { badge });
        Assert.False((bool)retype.Invoke(editor, new object[] { Key.A })!);

        type.GetMethod("Select", NP)!.Invoke(editor, new object?[] { null });
        canvas.Children.Clear();
    }

    private static void SaveProjectSmoke(EditorWindow editor, SettingsService settings, HistoryService history)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winshot-project-{Guid.NewGuid():N}.winshot");
        try
        {
            var saveProjectAsync = typeof(EditorWindow).GetMethod(
                "SaveProjectAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            WaitForTask((Task)saveProjectAsync.Invoke(editor, new object[] { path })!);
            Assert.True(System.IO.File.Exists(path));

            var reopened = EditorWindow.OpenProject(path, settings, history);
            Assert.NotNull(reopened);
            reopened!.Show();
            reopened.Close();
        }
        finally
        {
            try { System.IO.File.Delete(path); }
            catch { }
        }
    }

    /// <summary>
    /// Reproduction guard for "the editor shows a cropped image": a tall capture (e.g. a
    /// scrolling capture, or any shot larger than the editor viewport) must be fully fitted
    /// into the viewport on open, not shown 1:1 anchored top-left. Exercises the
    /// CreateForCapture path, which is the default at runtime.
    /// </summary>
    private static void EditorFitsTallImageSmoke(SettingsService settings, HistoryService history)
    {
        const int imgW = 1000;
        var tall = new SD.Bitmap(imgW, 4000);
        using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.CornflowerBlue);

        var editor = EditorWindow.CreateForCapture(tall, settings, history);
        editor.Show();
        for (int i = 0; i < 6; i++) PumpDispatcherOnce();

        var viewport = (System.Windows.FrameworkElement)editor.FindName("Viewport");
        double vw = viewport.ActualWidth, vh = viewport.ActualHeight;
        double zoom = (double)typeof(EditorWindow)
            .GetField("_zoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(editor)!;

        editor.Close();

        Assert.True(vw > 1 && vh > 1, $"viewport not laid out (size={vw}x{vh})");
        // A tall scrolling capture opens fit-WHOLE — the ENTIRE image (both dimensions) must be
        // visible, not opened at 1:1 or fit-to-width showing only the top slice.
        Assert.True(zoom * imgW <= vw + 1 && zoom * 4000 <= vh + 1,
            $"tall image not fit-whole on open: zoom={zoom}, scaled={zoom * imgW}x{zoom * 4000}, viewport={vw}x{vh}");
    }

    /// <summary>
    /// The grow entrance has to LAND on the rect it tweened to. MinWidth/MinHeight are
    /// zeroed so the tween can paint small frames and restored when it finishes, and
    /// Windows enforces them through WM_GETMINMAXINFO — so a target below the floor is
    /// resized back up on the next layout pass, which is the size snap the motion rule
    /// forbids (and, with animations off, simply the wrong opening size).
    /// </summary>
    private static void EditorGrowLandsOnItsTargetSmoke(SettingsService settings, HistoryService history)
    {
        const System.Reflection.BindingFlags NS =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

        var animations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false; // the tween applies its end frame inline
        EditorWindow? editor = null;
        try
        {
            var origin = new SD.Rectangle(40, 40, 200, 130);
            editor = EditorWindow.CreateForCapture(NewBitmap(), settings, history);
            double minWidth = editor.MinWidth, minHeight = editor.MinHeight;
            editor.GrowFrom(origin);
            editor.Show();
            for (int i = 0; i < 6; i++) PumpDispatcherOnce();

            double scale = RecordingMonitorDpi.ScaleFor(origin);
            SD.Rectangle work = WF.Screen.FromRectangle(origin).WorkingArea;
            double wantW = (double)typeof(EditorWindow).GetField("DefaultWidthDip", NS)!.GetRawConstantValue()!;
            double wantH = (double)typeof(EditorWindow).GetField("DefaultHeightDip", NS)!.GetRawConstantValue()!;
            SD.Rectangle target = EditorGrowLayout.Target(
                work, new Size(wantW, wantH), scale, new Size(minWidth, minHeight));

            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(editor).Handle, out RECT r);
            var landed = new SD.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

            Assert.True(Math.Abs(landed.Width - target.Width) <= 1 &&
                        Math.Abs(landed.Height - target.Height) <= 1,
                $"Editor grew to {landed} instead of its target {target}.");
            // The restored floor must be something the landed window can hold, and must fit
            // the monitor it opened on.
            Assert.True(editor.MinWidth <= target.Width / scale + 0.5,
                $"MinWidth {editor.MinWidth} exceeds the {target.Width / scale:0} DIP it landed at.");
            Assert.True(editor.MinWidth > 0 && editor.MinHeight > 0,
                "The grow left the editor with no minimum size at all.");
            Assert.Equal(0, Motion.Holders);
        }
        finally
        {
            try { editor?.Close(); } catch { }
            PumpDispatcherOnce();
            Motion.AnimationsEnabled = animations;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// A tiny capture must scale UP on Fit so the paper is the work, not a postage
    /// stamp in an empty viewport. 100% is a zoom preset, not what Fit means.
    /// </summary>
    private static void EditorFitsSmallImageSmoke(SettingsService settings, HistoryService history)
    {
        const int imgW = 80, imgH = 50;
        var small = new SD.Bitmap(imgW, imgH);
        using (var g = SD.Graphics.FromImage(small)) g.Clear(SD.Color.CornflowerBlue);

        var editor = EditorWindow.CreateForCapture(small, settings, history);
        editor.Show();
        for (int i = 0; i < 6; i++) PumpDispatcherOnce();

        var viewport = (System.Windows.FrameworkElement)editor.FindName("Viewport");
        double vw = viewport.ActualWidth, vh = viewport.ActualHeight;
        double zoom = (double)typeof(EditorWindow)
            .GetField("_zoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(editor)!;
        var fitMenuBtn = (Button)editor.FindName("FitMenuBtn")!;
        var zoomBox = (TextBox)editor.FindName("ZoomBox")!;

        editor.Close();

        Assert.True(vw > 1 && vh > 1, $"viewport not laid out (size={vw}x{vh})");
        Assert.True(zoom > 1.05, $"tiny capture stayed at native size: zoom={zoom}, viewport={vw}x{vh}");
        Assert.True(zoom * imgW <= vw + 1 && zoom * imgH <= vh + 1,
            $"tiny capture overflowed the viewport: zoom={zoom}, scaled={zoom * imgW}x{zoom * imgH}, viewport={vw}x{vh}");
        // Survey's fit insets: PAD=20 subtracted per side horizontally, GAP=16 folded into
        // the height DIVISOR (PdfjsViewerContainer.jsx:51-52, :137-140, :1891-1895).
        double surveyFit = Math.Min((vw - 40) / imgW, vh / (imgH + 32));
        Assert.True(Math.Abs(zoom - surveyFit) < 0.001,
            $"fit does not match Survey: zoom={zoom} expected={surveyFit} viewport={vw}x{vh}");
        // Survey's fit trigger tooltip is `Page fit: ${label}` in every state (AppShell.jsx:3072).
        Assert.Equal("Page fit: Fit page", fitMenuBtn.ToolTip);
        Assert.Equal(Visibility.Visible, zoomBox.Visibility);
        Assert.EndsWith("%", zoomBox.Text);
    }

    /// <summary>
    /// The props block must end inside the floating top bar. The zoom cluster moved to the
    /// right rail below the bar, so the bar's own right edge is the boundary now.
    /// </summary>
    private static Point BarRightEdge(EditorWindow editor)
    {
        var bar = (FrameworkElement)editor.FindName("TopToolbar")!;
        return bar.TranslatePoint(new Point(bar.ActualWidth - 6, 0), editor);
    }

    private static void PinSmoke(EditorWindow editor)
    {
        var onPin = typeof(EditorWindow).GetMethod(
            "OnPin",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        onPin.Invoke(editor, new object[] { editor, new RoutedEventArgs() });
        PumpDispatcherOnce();

        foreach (WF.Form form in WF.Application.OpenForms.Cast<WF.Form>().ToList())
        {
            if (form is FastPinWindow)
                form.Close();
        }
    }

    private static void BackgroundFromEditorSmoke(EditorWindow editor)
    {
        var onBackground = typeof(EditorWindow).GetMethod(
            "OnAddBackground",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        onBackground.Invoke(editor, new object[] { editor, new RoutedEventArgs() });
        PumpDispatcherOnce();
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToList())
            if (window is BackgroundComposerWindow)
                window.Close();
    }

    private static void QuickActionButtonSmoke(SettingsService settings)
    {
        using var overlay = new FastQuickActionsWindow(NewBitmap(), settings);
        bool pin = false;
        bool edit = false;
        bool ocr = false;
        bool background = false;

        overlay.PinRequested += _ => pin = true;
        overlay.EditRequested += _ => edit = true;
        overlay.OcrRequested += _ => ocr = true;
        overlay.BackgroundRequested += _ => background = true;

        InvokeQuickAction(overlay, "Pin");
        InvokeQuickAction(overlay, "Open Annotate");
        // OCR and Background remain local features but moved to the overflow menu to keep
        // the verified hover shell uncluttered. Exercise their legacy keyboard paths here.
        InvokeKey(overlay, System.Windows.Forms.Keys.O);
        InvokeKey(overlay, System.Windows.Forms.Keys.B);

        Assert.True(pin);
        Assert.True(edit);
        Assert.True(ocr);
        Assert.True(background);
    }

    private static void InvokeQuickAction(FastQuickActionsWindow overlay, string tipPrefix)
    {
        var buttonsField = typeof(FastQuickActionsWindow).GetField(
            "_buttons",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var buttons = (System.Collections.IEnumerable)buttonsField.GetValue(overlay)!;
        foreach (var button in buttons)
        {
            var tip = (string)button.GetType().GetProperty("Tip")!.GetValue(button)!;
            if (!tip.StartsWith(tipPrefix, StringComparison.Ordinal))
                continue;

            var action = (Action)button.GetType().GetProperty("Action")!.GetValue(button)!;
            action();
            return;
        }

        throw new InvalidOperationException($"Quick action not found: {tipPrefix}");
    }

    private static void InvokeKey(FastQuickActionsWindow overlay, System.Windows.Forms.Keys key)
    {
        var handler = typeof(FastQuickActionsWindow).GetMethod(
            "OnOverlayKeyDown",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(overlay, new object?[] { overlay, new System.Windows.Forms.KeyEventArgs(key) });
    }

    private static void WaitForEditorOperation(EditorWindow editor, System.Reflection.FieldInfo activeField)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((bool)activeField.GetValue(editor)! && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }

        Assert.False((bool)activeField.GetValue(editor)!);
    }

    /// <summary>
    /// Window.Close() hides asynchronously: the Closed handlers and visibility flip run on
    /// the dispatcher, so a single pump can race the assert (more likely now that the Settings
    /// window carries more controls to tear down). Pump until hidden, with a timeout.
    /// </summary>
    private static void WaitUntilHidden(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (window.IsVisible && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Pumps until <paramref name="settled"/> holds. Showing a HistoryWindow kicks off its own
    /// refresh (prune, then reload) and that reload cancels any other one in flight — which is
    /// right for the app and murder for a test that awaits its OWN ReloadAsync task and finds it
    /// returned early because the show-refresh took ownership. Let the window settle first.
    /// </summary>
    private static void WaitForCondition(Func<bool> settled, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!settled() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }

        Assert.True(settled(), because);
    }

    private static void WaitForTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }

        Assert.True(task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Switching tabs cross-fades instead of hard-cutting, and the title follows the tab.
    /// The assertion that matters after the animation is that exactly one pane is left
    /// visible at full opacity — a fade that forgets to collapse its outgoing pane leaves
    /// two stacked ScrollViewers and a window that scrolls the wrong content.
    /// </summary>
    private static void SettingsSectionMotionSmoke(SettingsWindow window)
    {
        var list = (System.Windows.Controls.ListBox)window.FindName("SectionList")!;
        string[] paneNames =
        [
            "SectionGeneral", "SectionShortcuts", "SectionQuickAccess", "SectionRecording",
            "SectionScreenshots", "SectionAnnotate", "SectionAdvanced", "SectionAbout",
        ];

        Assert.Contains("General", window.Title);

        var general = (ScrollViewer)window.FindName("SectionGeneral")!;
        var screenshots = (ScrollViewer)window.FindName("SectionScreenshots")!;

        // Animated pass. Only what the swap does SYNCHRONOUSLY is asserted here: the
        // incoming pane is up, and the outgoing one has stopped taking input. Everything
        // after that is a WPF animation clock running on wall time, and an assertion that
        // races it fails the moment another test class puts load on the machine.
        list.SelectedIndex = 4;
        PumpDispatcherOnce();
        Assert.Contains("Screenshots", window.Title);
        Assert.Equal(Visibility.Visible, screenshots.Visibility);
        Assert.True(screenshots.IsHitTestVisible);
        // The panes are overlapping siblings and WPF hit-tests at Opacity 0 exactly as it
        // does at 1, so a fading-out pane that is still hit-testable swallows every click
        // and wheel event aimed at the tab you just picked.
        Assert.False(general.IsHitTestVisible, "the outgoing pane still takes input mid-fade");

        // Deterministic pass. With animation off every transition collapses to its end
        // state inside one dispatcher turn, so the end state can be asserted exactly.
        Func<bool> animations = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            list.SelectedIndex = 0;
            PumpDispatcherOnce();
            list.SelectedIndex = 4;
            PumpDispatcherOnce();

            var live = paneNames
                .Select(n => (name: n, pane: (ScrollViewer)window.FindName(n)!))
                .Where(x => x.pane.Visibility == Visibility.Visible)
                .ToArray();
            Assert.Single(live);
            Assert.Equal("SectionScreenshots", live[0].name);
            Assert.Equal(1d, live[0].pane.Opacity, 3);
            // General was re-armed on its way in and disarmed again on its way out; the pane
            // now showing is the one that has to be taking input.
            Assert.True(screenshots.IsHitTestVisible, "the visible pane must take input");
            Assert.False(general.IsHitTestVisible);

            // The About pane's disclosures start closed, reveal what they name, and fade
            // back out rather than snapping — which is why this half needs animations off.
            var diagnostics = (ToggleButton)window.FindName("AboutDiagnosticsToggle")!;
            var diagnosticsPanel = (StackPanel)window.FindName("AboutDiagnosticsPanel")!;
            var notices = (ToggleButton)window.FindName("AboutNoticesToggle")!;
            var noticesPanel = (StackPanel)window.FindName("AboutNoticesPanel")!;
            Assert.Equal(Visibility.Collapsed, diagnosticsPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, noticesPanel.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("AboutNoticesText")!).Text));
            diagnostics.IsChecked = true;
            notices.IsChecked = true;
            PumpDispatcherOnce();
            Assert.Equal(Visibility.Visible, diagnosticsPanel.Visibility);
            Assert.Equal(Visibility.Visible, noticesPanel.Visibility);
            diagnostics.IsChecked = false;
            notices.IsChecked = false;
            PumpDispatcherOnce();
            Assert.Equal(Visibility.Collapsed, diagnosticsPanel.Visibility);

            // Back to General so the rest of the smoke sees the window it expects.
            list.SelectedIndex = 0;
            PumpDispatcherOnce();
        }
        finally
        {
            Motion.AnimationsEnabled = animations;
        }
        Assert.Contains("General", window.Title);
    }

    /// <summary>
    /// Checkboxes and combos apply as they change — no Done — while typed fields still
    /// wait for a commit, and "Restore defaults" resets the visible tab only.
    /// </summary>
    private static void SettingsLiveApplyAndDefaultsSmoke(SettingsWindow window, SettingsService settings)
    {
        var list = (ListBox)window.FindName("SectionList")!;
        var overlayCheck = (CheckBox)window.FindName("ScreenshotShowOverlayCheck")!;
        var pinCheck = (CheckBox)window.FindName("ScreenshotPinCheck")!;
        var freeze = (CheckBox)window.FindName("FreezeScreenCheck")!;
        var template = (TextBox)window.FindName("TemplateBox")!;

        // A toggle applies without touching Done.
        bool before = settings.Current.ScreenshotPin;
        pinCheck.IsChecked = !before;
        window.FlushLiveApplyForTest();
        Assert.Equal(!before, settings.Current.ScreenshotPin);

        // A typed field does NOT — that is what Done is still for, and what Escape warns about.
        string storedTemplate = settings.Current.FileNameTemplate;
        Assert.False(window.HasUncommittedFieldEditsForTest());
        template.Text = storedTemplate + " draft";
        Assert.True(window.HasUncommittedFieldEditsForTest());
        window.FlushLiveApplyForTest();
        Assert.Equal(storedTemplate, settings.Current.FileNameTemplate);
        template.Text = storedTemplate;
        Assert.False(window.HasUncommittedFieldEditsForTest());

        // Restore defaults resets the tab it is on, and nothing else.
        settings.Current.FreezeScreen = false;
        freeze.IsChecked = false;
        overlayCheck.IsChecked = false;
        window.FlushLiveApplyForTest();
        Assert.False(settings.Current.ScreenshotShowOverlay);

        list.SelectedIndex = 0;                       // General owns the after-capture matrix
        PumpDispatcherOnce();
        window.RestoreSectionDefaults(0);
        Assert.True(settings.Current.ScreenshotShowOverlay);   // reset
        Assert.False(settings.Current.FreezeScreen);           // Screenshots tab untouched

        window.RestoreSectionDefaults(4);                      // now reset Screenshots
        Assert.True(settings.Current.FreezeScreen);

        // The Shortcuts tab is nothing BUT hotkey boxes, and WriteToggleSettings does not
        // reach them: the button used to show a confirm, clear every box and write nothing.
        settings.Current.HotkeyCaptureRegion = "Ctrl+Shift+F9";
        list.SelectedIndex = 1;
        PumpDispatcherOnce();
        window.RestoreSectionDefaults(1);
        Assert.Equal(new Settings().HotkeyCaptureRegion, settings.Current.HotkeyCaptureRegion);
    }

    private static void PumpDispatcherOnce()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static SD.Bitmap NewBitmap()
    {
        var bitmap = new SD.Bitmap(80, 50);
        using var g = SD.Graphics.FromImage(bitmap);
        g.Clear(SD.Color.CornflowerBlue);
        return bitmap;
    }

    private sealed class TempImageFile : IDisposable
    {
        public TempImageFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"winshot-test-{Guid.NewGuid():N}.png");
            using var bitmap = NewBitmap();
            bitmap.Save(Path, SD.Imaging.ImageFormat.Png);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.File.Delete(Path); }
            catch { }
        }
    }
}
