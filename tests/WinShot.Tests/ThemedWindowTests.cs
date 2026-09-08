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

        ((Button)window.FindName("AllSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(5, list.SelectedItems.Count);
        ((Button)window.FindName("AllSelectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(list.SelectedItems.Cast<HistoryItem>());
        ((Button)window.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(5, list.SelectedItems.Count);

        window.Close();
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

    private static void AssertSettingsTruthfulAndAccessible(SettingsWindow window)
    {
        T Named<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(window.FindName(name));

        Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
        Assert.True(window.MinWidth >= 620);
        Assert.True(window.MinHeight >= 560);

        Assert.Equal(Visibility.Visible, Named<System.Windows.Controls.ListBoxItem>("QuickAccessNavItem").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<System.Windows.Controls.ListBoxItem>("AnnotateNavItem").Visibility);
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

    private static void HistoryWindowShowCloseSmoke(HistoryService history, SettingsService settings)
    {
        var window = HistoryWindow.Show(history, settings);
        Assert.True(window.IsVisible);
        Assert.True(window.ShowInTaskbar);
        window.Close();
        WaitUntilHidden(window);
        Assert.False(window.IsVisible);
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
