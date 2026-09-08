using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                ShowAndClose(new VideoEditorWindow(previewFile.Path, settings, history));
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
                ApplyBlurUndoRedoSmoke(editor);
                ZOrderReorderSmoke(editor);
                SaveProjectSmoke(editor, settings, history);
                PinSmoke(editor);
                BackgroundFromEditorSmoke(editor);
                editor.Close();

                var composer = new BackgroundComposerWindow(NewBitmap(), settings, history);
                composer.Show();
                composer.Close();

                EditorFitsTallImageSmoke(settings, history);

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

    private static void HistorySelectionInteractionSmoke(HistoryService history, SettingsService settings)
    {
        var window = new HistoryWindow(history, settings);
        const System.Reflection.BindingFlags NP =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
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
        settings.Current.PostCaptureAction = "save";
        var window = SettingsWindow.Show(settings);
        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        AssertSettingsTruthfulAndAccessible(window);
        Assert.Equal(
            "never",
            ((System.Windows.Controls.ComboBoxItem)
                ((System.Windows.Controls.ComboBox)window.FindName("CrosshairModeCombo")).SelectedItem).Tag);
        Assert.Equal(
            "save",
            ((System.Windows.Controls.ComboBoxItem)
                ((System.Windows.Controls.ComboBox)window.FindName("PostCaptureActionCombo")).SelectedItem).Tag);
        typeof(SettingsWindow).GetMethod(
            "SaveShortcutBoxes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, new object[] { settings.Current });
        Assert.Equal("Ctrl+Alt+Q", settings.Current.ShortcutBindings["legacy-placeholder"]);
        window.Close();
        WaitUntilHidden(window);
        Assert.False(window.IsVisible);
    }

    private static void AssertSettingsTruthfulAndAccessible(SettingsWindow window)
    {
        T Named<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(window.FindName(name));

        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.True(window.MinWidth >= 620);
        Assert.True(window.MinHeight >= 560);

        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.ListBoxItem>("QuickAccessNavItem").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.ListBoxItem>("AnnotateNavItem").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.Border>("LegacyAfterCaptureMatrix").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.DockPanel>("UnavailableSoundsRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.DockPanel>("MaxResolutionRow").Visibility);
        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.Border>("PinnedStyleCard").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.Border>("UnavailableCopyFormatCard").Visibility);

        var done = Named<System.Windows.Controls.Button>("SaveButton");
        var cancel = Named<System.Windows.Controls.Button>("CancelButton");
        Assert.True(done.IsDefault);
        Assert.True(cancel.IsCancel);
        Assert.NotNull(done.FocusVisualStyle);
        Assert.NotNull(cancel.FocusVisualStyle);

        string[] accessibleControls =
        [
            "StartupCheck", "UpdatesCheck", "SaveFolderBox", "BrowseSaveFolderButton",
            "HideIconsCheck", "PostCaptureActionCombo", "PostCaptureCopyCheck", "OverlayPositionCombo",
            "OverlayMoveToActiveScreenCheck", "OverlaySizeSlider", "OverlayAutoCloseCheck",
            "OverlayActionCombo", "OverlayCloseBox", "OverlayCloseAfterDragCheck", "OverlaySaveBehaviorCombo",
            "ShowRecordingControlsCheck", "ShowRecordingTimerCheck", "ScaleHiDpiVideoCheck", "CaptureCursorCheck",
            "ClickHighlightsCheck", "KeystrokesCheck", "RememberLastSelectionCheck", "DimScreenCheck",
            "CountdownBox", "WebcamCombo", "WebcamSizeBox", "RecordingShowOverlayCheck",
            "RecordingCopyCheck", "MaxResolutionCombo", "RecordingFpsBox", "RecordAudioCheck",
            "SystemAudioCheck", "RecordAudioMonoCheck", "OpenVideoEditorCheck", "GifFpsBox",
            "GifQualitySlider", "OptimizeGifsCheck", "GifSizeCombo", "FormatCombo",
            "HiDpiCheck", "SelfTimerCombo", "FreezeScreenCheck", "CrosshairModeCombo",
            "ShowMagnifierCheck", "TemplateBox", "HistorySlider", "PinnedRoundedCornersCheck",
            "PinnedShadowCheck", "PinnedBorderCheck", "AllInOneRememberCheck",
            "OcrJoinLinesCheck", "CancelButton", "SaveButton", "AboutCheckUpdatesButton",
            "AboutRepositoryButton", "AboutLogsButton",
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
            Named<System.Windows.Controls.Control>("PostCaptureActionCombo").TabIndex,
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
        var primary = (WrapPanel)editor.FindName("ToolPanel")!;
        var primaryOrder = primary.Children.OfType<RadioButton>()
            .Select(rb => (string)rb.Tag)
            .ToArray();
        Assert.Equal(EditorShellContract.PrimaryToolOrder, primaryOrder);

        var more = (UniformGrid)editor.FindName("MoreToolPanel")!;
        var moreOrder = more.Children.OfType<RadioButton>()
            .Select(rb => (string)rb.Tag)
            .ToArray();
        Assert.Equal(EditorShellContract.MoreToolOrder, moreOrder);

        string[] namedControls =
        {
            "CropUtilityBtn", "AddImageBtn", "AddBackgroundBtn", "SelectToolBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "PixelateToolBtn", "SpotlightToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn",
            "MoreToolsButton", "PanToolBtn", "CurvedArrowToolBtn", "BlurToolBtn", "EyedropperToolBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "BtnUndo", "BtnRedo", "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnSave", "BtnDone", "BtnPin", "BtnCopy", "BtnCenter",
        };
        foreach (string name in namedControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), name);
        }

        string[] iconOnlyControls =
        {
            "CropUtilityBtn", "AddImageBtn", "AddBackgroundBtn", "SelectToolBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "PixelateToolBtn", "SpotlightToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn",
            "MoreToolsButton", "PanToolBtn", "CurvedArrowToolBtn", "BlurToolBtn", "EyedropperToolBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "BtnUndo", "BtnRedo", "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnPin", "BtnCopy", "BtnCenter",
        };
        foreach (string name in iconOnlyControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)), name);
        }

        Assert.Null(editor.FindName("EmojiToolBtn"));
        Assert.Null(editor.FindName("DragMeHandle"));

        var select = (RadioButton)editor.FindName("SelectToolBtn")!;
        var moreButton = (Button)editor.FindName("MoreToolsButton")!;
        var copy = (Button)editor.FindName("BtnCopy")!;
        foreach (Control control in new Control[] { select, moreButton, copy })
        {
            control.ApplyTemplate();
            Assert.NotNull(control.Template.FindName("FocusRing", control));
            Assert.True(control.Focusable);
            Assert.True(control.IsTabStop);
        }

        var styleBar = (DockPanel)editor.FindName("EditorStyleBar")!;
        var color = (StackPanel)editor.FindName("ColorPanel")!;
        var thickness = (StackPanel)editor.FindName("ThicknessPanel")!;
        var fill = (StackPanel)editor.FindName("FillPanel")!;
        var arrowStyle = (StackPanel)editor.FindName("ArrowStylePanel")!;
        var effects = (StackPanel)editor.FindName("EffectStrengthPanel")!;
        var cropRatio = (StackPanel)editor.FindName("CropRatioPanel")!;
        var sizeBox = (TextBox)editor.FindName("SizeBox")!;
        var sizeLabel = (TextBlock)editor.FindName("ThicknessLabel")!;
        var sizePresets = (StackPanel)editor.FindName("SizePresetList")!;

        Assert.Equal(Visibility.Collapsed, styleBar.Visibility);
        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Visible, arrowStyle.Visibility);
        Assert.Equal(Visibility.Collapsed, fill.Visibility);
        Assert.Equal("Width", sizeLabel.Text);

        // The size field takes any whole number, not just the old 2 / 4 / 6.
        sizeBox.Text = "17";
        Assert.Equal("17", sizeBox.Text);

        // Out-of-range input is clamped to the stored value rather than rejected. The box
        // keeps the raw text while the user is mid-type and normalises on commit, so the
        // clamp shows up the next time the field is re-synced.
        sizeBox.Text = "999";
        ((RadioButton)editor.FindName("LineToolBtn")!).IsChecked = true;
        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal("50", sizeBox.Text);
        sizeBox.Text = "7";

        // The preset menu offers Survey's twelve widths, each row carrying its value.
        Assert.Equal(
            AnnotationSize.Presets,
            sizePresets.Children.OfType<RadioButton>().Select(r => (int)r.Tag!).ToArray());

        // Each tool keeps its own size: Text is independent of Arrow.
        ((RadioButton)editor.FindName("TextToolBtn")!).IsChecked = true;
        Assert.Equal("Size", sizeLabel.Text);
        Assert.Equal("4", sizeBox.Text);

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal("Width", sizeLabel.Text);
        Assert.Equal("7", sizeBox.Text);

        ((RadioButton)editor.FindName("FilledRectangleToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Collapsed, thickness.Visibility);
        Assert.Equal(Visibility.Visible, fill.Visibility);

        ((RadioButton)editor.FindName("PixelateToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, effects.Visibility);
        Assert.Equal(Visibility.Collapsed, color.Visibility);

        ((RadioButton)editor.FindName("CropUtilityBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, cropRatio.Visibility);
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;

        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        var toolbar = (Border)editor.FindName("TopToolbar")!;
        Assert.True(toolbar.ActualWidth <= editor.ActualWidth);
        Assert.True(primary.ActualHeight <= 42, $"Primary tools wrapped at minimum width: {primary.ActualHeight}");
    }

    private static void ApplyBlurUndoRedoSmoke(EditorWindow editor)
    {
        var type = typeof(EditorWindow);
        var activeField = type.GetField(
            "_sourceOperationActive",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var applyBlur = type.GetMethod(
            "ApplyBlur",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var undoAsync = type.GetMethod(
            "UndoAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var redoAsync = type.GetMethod(
            "RedoAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        WaitForEditorOperation(editor, activeField);
        applyBlur.Invoke(editor, new object[] { new SD.Rectangle(0, 0, 40, 40) });
        WaitForEditorOperation(editor, activeField);
        WaitForTask((Task)undoAsync.Invoke(editor, Array.Empty<object>())!);
        WaitForEditorOperation(editor, activeField);
        WaitForTask((Task)redoAsync.Invoke(editor, Array.Empty<object>())!);
        WaitForEditorOperation(editor, activeField);
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
