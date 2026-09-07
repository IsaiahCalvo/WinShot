using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
                RedactionObjectSmoke(editor);
                AnnotationClipboardSmoke(editor);
                TextAndCounterSmoke(editor);
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
        // The toolbar is one row now: undo/redo, the pointer pair, five categories, then
        // the active tool's own controls. Nothing behind a "More" popup.
        Assert.Null(editor.FindName("MoreToolsButton"));
        Assert.Null(editor.FindName("MoreToolsPopup"));
        Assert.Null(editor.FindName("MoreToolPanel"));
        Assert.Null(editor.FindName("CurvedArrowToolBtn"));
        Assert.Null(editor.FindName("EmojiToolBtn"));
        Assert.Null(editor.FindName("DragMeHandle"));

        var categories = (StackPanel)editor.FindName("CategoryPanel")!;
        Assert.Equal(
            EditorShellContract.CategoryOrder.Select(c => c.ToString()).ToArray(),
            categories.Children.OfType<RadioButton>().Select(rb => (string)rb.Tag).ToArray());

        foreach (EditorToolCategory category in EditorShellContract.CategoryOrder)
        {
            var row = (StackPanel)editor.FindName($"{category}ToolRow")!;
            Assert.Equal(
                EditorShellContract.ToolsIn(category),
                row.Children.OfType<RadioButton>().Select(rb => (string)rb.Tag).ToArray());
        }

        string[] namedControls =
        {
            "BtnUndo", "BtnRedo", "SelectToolBtn", "SelectModeButton", "PanToolBtn",
            "CategoryDrawBtn", "CategoryShapesBtn", "CategoryTextBtn", "CategoryRedactBtn", "CategoryImageBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "RedactToolBtn", "SpotlightToolBtn", "MagnifierToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EyedropperToolBtn",
            "CropUtilityBtn", "AddImageBtn", "AddCaptureBtn", "AddBackgroundBtn", "OcrRegionBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "SnapToEdgesCheck", "RedactTextOnlyCheck", "DashToolBtn", "SketchToolBtn", "MagnifySlider",
            "CurrentColorIndicator", "ArrowStyleButton", "TextStyleButton", "CropRatioButton", "RedactStyleButton",
            "BorderTabBtn", "FillTabBtn", "SaveColorBtn", "PickerEyedropperBtn", "HueSlider", "AlphaSlider",
            "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnSave", "BtnDone", "BtnPin", "BtnCopy", "BtnCenter",
        };
        foreach (string name in namedControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), name);
        }

        // Every control that shows only a glyph has to say what it does out loud.
        string[] iconOnlyControls =
        {
            "BtnUndo", "BtnRedo", "SelectToolBtn", "SelectModeButton", "PanToolBtn",
            "CategoryDrawBtn", "CategoryShapesBtn", "CategoryTextBtn", "CategoryRedactBtn", "CategoryImageBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "RedactToolBtn", "SpotlightToolBtn", "MagnifierToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EyedropperToolBtn",
            "CropUtilityBtn", "AddImageBtn", "AddCaptureBtn", "AddBackgroundBtn", "OcrRegionBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn",
            "SnapToEdgesCheck", "RedactTextOnlyCheck", "DashToolBtn", "SketchToolBtn",
            "CurrentColorIndicator", "ArrowStyleButton", "TextStyleButton", "CropRatioButton", "RedactStyleButton",
            "SaveColorBtn", "PickerEyedropperBtn",
            "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "StepNumberBtn", "StepLetterBtn",
            "BtnPin", "BtnCopy", "BtnCenter",
        };
        foreach (string name in iconOnlyControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)), name);
        }

        // Every popover animates in from its anchor edge, which needs its own (unfrozen)
        // ScaleTransform on the surface — a Style-set one would be shared and frozen, and
        // animating a frozen Freezable throws the first time a popup opens.
        foreach (string name in new[]
        {
            "SelectModePopup", "ColorPickerPopup", "SizePresetPopup",
            "ArrowStylePopup", "TextStylePopup", "CropRatioPopup", "RedactStylePopup",
        })
        {
            var popup = Assert.IsType<Popup>(editor.FindName(name));
            var surface = Assert.IsType<Border>(popup.Child);
            Assert.IsType<ScaleTransform>(surface.RenderTransform);
            Assert.False(surface.RenderTransform.IsFrozen, name);
        }

        var select = (RadioButton)editor.FindName("SelectToolBtn")!;
        var undo = (Button)editor.FindName("BtnUndo")!;
        var copy = (Button)editor.FindName("BtnCopy")!;
        foreach (Control control in new Control[] { select, undo, copy })
        {
            control.ApplyTemplate();
            Assert.NotNull(control.Template.FindName("FocusRing", control));
            Assert.True(control.Focusable);
            Assert.True(control.IsTabStop);
        }

        var context = (StackPanel)editor.FindName("ContextPanel")!;
        var subRow = (Border)editor.FindName("CategoryToolRow")!;
        var color = (StackPanel)editor.FindName("ColorPanel")!;
        var thickness = (StackPanel)editor.FindName("ThicknessPanel")!;
        var fill = (StackPanel)editor.FindName("FillPanel")!;
        var arrowStyle = (StackPanel)editor.FindName("ArrowStylePanel")!;
        var zoomButton = (Button)editor.FindName("ZoomMenuButton")!;
        var zoomLabel = (TextBlock)editor.FindName("ZoomLabel")!;
        Assert.Null(editor.FindName("ZoomBox"));
        var redactStyle = (StackPanel)editor.FindName("RedactStylePanel")!;
        var redactStrength = (StackPanel)editor.FindName("RedactStrengthPanel")!;
        var cropRatio = (StackPanel)editor.FindName("CropRatioPanel")!;
        var cropTools = (StackPanel)editor.FindName("CropToolsPanel")!;
        var sizeBox = (TextBox)editor.FindName("SizeBox")!;
        var sizeField = (Border)editor.FindName("SizeFieldBorder")!;
        var sizeHeading = (TextBlock)editor.FindName("SizePresetHeading")!;
        var sizePresets = (StackPanel)editor.FindName("SizePresetList")!;
        var shapesRow = (StackPanel)editor.FindName("ShapesToolRow")!;
        var drawRow = (StackPanel)editor.FindName("DrawToolRow")!;

        // The pointer tool has no controls of its own, so the row disappears entirely.
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Collapsed, context.Visibility);

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, context.Visibility);
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Visible, arrowStyle.Visibility);
        Assert.Equal(Visibility.Collapsed, fill.Visibility);
        Assert.Equal("Width", sizeHeading.Text);

        // Arming a tool lights its category and swaps the floating sub-row to that row.
        Assert.True(((RadioButton)editor.FindName("CategoryShapesBtn")!).IsChecked);
        Assert.Equal(Visibility.Visible, subRow.Visibility);
        Assert.Equal(Visibility.Visible, shapesRow.Visibility);
        Assert.Equal(Visibility.Collapsed, drawRow.Visibility);

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

        // Each tool keeps its own size: Text is independent of Arrow. The word "Width" /
        // "Size" left the toolbar with the other labels and lives on the field instead.
        ((RadioButton)editor.FindName("TextToolBtn")!).IsChecked = true;
        Assert.Equal("Size", sizeHeading.Text);
        Assert.Equal("Size", sizeField.ToolTip);
        Assert.Equal("4", sizeBox.Text);
        Assert.True(((RadioButton)editor.FindName("CategoryTextBtn")!).IsChecked);

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Equal("Width", sizeHeading.Text);
        Assert.Equal("7", sizeBox.Text);

        ((RadioButton)editor.FindName("FilledRectangleToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Collapsed, thickness.Visibility);
        Assert.Equal(Visibility.Visible, fill.Visibility);

        // One redaction tool with a style menu and a continuous strength — no S/M/L radios,
        // and no colour well, because a redaction has no colour to pick.
        ((RadioButton)editor.FindName("RedactToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, redactStyle.Visibility);
        Assert.Equal(Visibility.Visible, redactStrength.Visibility);
        Assert.Equal(Visibility.Collapsed, color.Visibility);
        Assert.True(((RadioButton)editor.FindName("CategoryRedactBtn")!).IsChecked);

        ((RadioButton)editor.FindName("CropUtilityBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, cropRatio.Visibility);
        Assert.Equal(Visibility.Visible, cropTools.Visibility);
        // The crop bar says how big the picture is, so "Image size" is an answer as well
        // as a button.
        Assert.Contains("×", ((TextBlock)editor.FindName("ImageSizeLabel")!).Text);
        Assert.True(((RadioButton)editor.FindName("CategoryImageBtn")!).IsChecked);

        // A category button opens its own row and arms that row's first tool; Image is the
        // one that arms nothing, so clicking it cannot drop the user into a crop.
        ((RadioButton)editor.FindName("CategoryDrawBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, drawRow.Visibility);
        Assert.True(((RadioButton)editor.FindName("FreehandToolBtn")!).IsChecked);
        ((RadioButton)editor.FindName("CategoryImageBtn")!).IsChecked = true;
        Assert.True(((RadioButton)editor.FindName("FreehandToolBtn")!).IsChecked);

        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;

        // The zoom readout is written in one place, on every view change — the old label +
        // combo pair went stale after a wheel zoom and never came back.
        typeof(EditorWindow).GetMethod("ZoomToPercent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { 2.0 });
        Assert.Equal("200%", zoomLabel.Text);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(zoomButton)));

        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        var toolbar = (Border)editor.FindName("TopToolbar")!;
        Assert.True(toolbar.ActualWidth <= editor.ActualWidth);
        // One row, always: the contextual controls are inline now, so nothing can wrap
        // onto a second line and shove the canvas down mid-edit.
        var primaryRow = (DockPanel)editor.FindName("ToolbarPrimaryRow")!;
        Assert.True(primaryRow.ActualHeight <= 42, $"Primary toolbar wrapped at minimum width: {primaryRow.ActualHeight}");
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
