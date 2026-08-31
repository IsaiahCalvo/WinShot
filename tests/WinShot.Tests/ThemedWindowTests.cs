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
        var primary = (StackPanel)editor.FindName("ToolPanel")!;
        var primaryOrder = primary.Children.OfType<FrameworkElement>()
            .Select(el => el.Tag as string)
            .Where(tag => tag is "Pan" or "Select" or "Draw" or "Shape" or "Text" or "Eyedropper" or "Pixelate" or "Spotlight" or "Blur")
            .ToArray();
        Assert.Equal(EditorShellContract.PrimaryToolOrder, primaryOrder);

        Assert.Null(editor.FindName("MoreToolPanel"));
        Assert.Null(editor.FindName("MoreToolsButton"));

        string[] namedControls =
        {
            "CropUtilityBtn", "AddImageBtn", "AddBackgroundBtn", "SelectToolBtn",
            "DrawGroupBtn", "ShapeGroupBtn", "TextGroupBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "CalloutToolBtn", "PixelateToolBtn", "SpotlightToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EraserToolBtn",
            "PanToolBtn", "CurvedArrowToolBtn", "BlurToolBtn", "EyedropperToolBtn",
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
            "DrawGroupBtn", "ShapeGroupBtn", "TextGroupBtn",
            "RectangleToolBtn", "FilledRectangleToolBtn", "EllipseToolBtn", "LineToolBtn",
            "ArrowToolBtn", "TextToolBtn", "CalloutToolBtn", "PixelateToolBtn", "SpotlightToolBtn",
            "StepToolBtn", "FreehandToolBtn", "HighlighterToolBtn", "EraserToolBtn",
            "PanToolBtn", "CurvedArrowToolBtn", "BlurToolBtn", "EyedropperToolBtn",
            "RotateCcwBtn", "RotateCwBtn", "FlipHorizontalBtn", "FlipVerticalBtn", "ResizeImageBtn",
            "BtnUndo", "BtnRedo", "FillNoneBtn", "FillQuarterBtn", "FillSolidBtn",
            "BtnPin", "BtnCopy", "BtnCenter", "MoreChromeBtn", "AlignMenuBtn",
        };
        foreach (string name in iconOnlyControls)
        {
            var element = Assert.IsAssignableFrom<FrameworkElement>(editor.FindName(name));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)), name);
        }

        Assert.Null(editor.FindName("EmojiToolBtn"));
        Assert.Null(editor.FindName("DragMeHandle"));

        var select = (RadioButton)editor.FindName("SelectToolBtn")!;
        var pan = (RadioButton)editor.FindName("PanToolBtn")!;
        var copy = (Button)editor.FindName("BtnCopy")!;
        foreach (Control control in new Control[] { select, pan, copy })
        {
            control.ApplyTemplate();
            Assert.NotNull(control.Template.FindName("FocusRing", control));
            Assert.True(control.Focusable);
            Assert.True(control.IsTabStop);
        }

        Assert.Null(editor.FindName("EditorStyleBar"));
        var groupBar = (FrameworkElement)editor.FindName("EditorGroupBar")!;
        var groupTools = (StackPanel)editor.FindName("EditorGroupTools")!;
        var colorBar = (StackPanel)editor.FindName("EditorColorBar")!;
        var formatBar = (StackPanel)editor.FindName("EditorFormatBar")!;
        var color = (StackPanel)editor.FindName("ColorPanel")!;
        var thickness = (StackPanel)editor.FindName("ThicknessPanel")!;
        var fill = (StackPanel)editor.FindName("FillPanel")!;
        var arrowStyle = (StackPanel)editor.FindName("ArrowStylePanel")!;
        var effects = (StackPanel)editor.FindName("EffectStrengthPanel")!;
        var cropRatio = (StackPanel)editor.FindName("CropRatioPanel")!;
        var sizeBox = (ComboBox)editor.FindName("SizeBox")!;
        var viewport = (Grid)editor.FindName("Viewport")!;
        var undo = (Button)editor.FindName("BtnUndo")!;
        var topToolbar = (Border)editor.FindName("TopToolbar")!;
        Assert.Equal(HorizontalAlignment.Center, primary.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Left, groupTools.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Center, formatBar.HorizontalAlignment);
        var toolsHost = (FrameworkElement)editor.FindName("EditorGroupToolsHost")!;
        var formatHost = (FrameworkElement)editor.FindName("EditorFormatHost")!;
        Assert.Equal(HorizontalAlignment.Center, toolsHost.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Center, formatHost.HorizontalAlignment);
        Assert.True(toolsHost.MinWidth >= 180, "Group tools slot must stay as wide as Shapes so Draw/Text do not jump.");
        Assert.True(IsLogicalDescendant(topToolbar, undo), "Undo must live on the top toolbar, not over the image.");
        Assert.True(IsLogicalDescendant(topToolbar, (DependencyObject)editor.FindName("RotateCcwBtn")!),
            "Rotate must live next to Crop, not over the image.");
        Assert.DoesNotContain(((Grid)viewport.Parent).Children.OfType<FrameworkElement>(),
            el => el.Name is "EditorStyleBar");

        Assert.Equal(Visibility.Collapsed, groupBar.Visibility);
        Assert.DoesNotContain(primary.Children.OfType<FrameworkElement>(),
            el => el.Tag as string == "Step");
        Assert.Contains(((StackPanel)editor.FindName("ShapeGroupPanel")!).Children.OfType<RadioButton>(),
            rb => rb.Tag as string == "Step");
        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        Assert.Equal(Visibility.Visible, groupBar.Visibility);
        double arrowToolsX = groupTools.TranslatePoint(new Point(0, 0), groupBar).X;
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("ShapeGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Visible, colorBar.Visibility);
        Assert.Equal(Visibility.Visible, formatBar.Visibility);
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Visible, arrowStyle.Visibility);
        Assert.Equal(Visibility.Collapsed, fill.Visibility);
        Assert.Null(editor.FindName("EraserPartialBtn"));
        Assert.NotNull(editor.FindName("EraserModeBox"));
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
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "2");
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "50");

        ((RadioButton)editor.FindName("TextToolBtn")!).IsChecked = true;
        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        var top = (Border)editor.FindName("TopToolbar")!;
        var primaryRow = (Grid)editor.FindName("ToolbarPrimaryRow")!;
        Assert.True(primaryRow.ActualHeight <= 48,
            $"Primary row grew instead of staying one line: {primaryRow.ActualHeight}");
        Assert.True(top.ActualHeight <= 140,
            $"Top chrome stacked too many rows with Text selected: {top.ActualHeight}");
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("TextGroupPanel")!).Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("ShapeGroupPanel")!).Visibility);
        Assert.Equal("Size", ((TextBlock)editor.FindName("ThicknessLabel")!).Text);
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "16");
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "72");
        var fontSizeBox = (ComboBox)editor.FindName("FontSizeBox")!;
        Assert.Equal(Visibility.Collapsed, fontSizeBox.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("LineStylePanel")!).Visibility);
        Point toolsOrigin = groupTools.TranslatePoint(new Point(0, 0), groupBar);
        Point extrasOrigin = formatBar.TranslatePoint(new Point(0, 0), groupBar);
        Assert.True(toolsOrigin.Y + groupTools.ActualHeight <= extrasOrigin.Y + 0.5,
            $"Group tools overlap format extras: toolsY={toolsOrigin.Y}+{groupTools.ActualHeight} extrasY={extrasOrigin.Y}");
        Assert.True(Math.Abs(toolsOrigin.X - arrowToolsX) < 2,
            $"Group tools jumped when switching Arrow → Text: {arrowToolsX:0} → {toolsOrigin.X:0}");

        ((RadioButton)editor.FindName("CalloutToolBtn")!).IsChecked = true;
        editor.UpdateLayout();
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "1");
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "50");
        Assert.DoesNotContain(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "72");
        Assert.Equal(Visibility.Visible, fontSizeBox.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal("Stroke", ((TextBlock)editor.FindName("ThicknessLabel")!).Text);
        Point calloutTools = groupTools.TranslatePoint(new Point(0, 0), groupBar);
        Point calloutExtras = formatBar.TranslatePoint(new Point(0, 0), groupBar);
        Assert.True(calloutTools.Y + groupTools.ActualHeight <= calloutExtras.Y + 0.5,
            $"Callout tools overlap format extras: toolsY={calloutTools.Y}+{groupTools.ActualHeight} extrasY={calloutExtras.Y}");
        var save = (Button)editor.FindName("BtnSave")!;
        Point strokeOrigin = thickness.TranslatePoint(new Point(0, 0), editor);
        Point saveOrigin = save.TranslatePoint(new Point(0, 0), editor);
        Assert.True(strokeOrigin.X + thickness.ActualWidth <= saveOrigin.X + 0.5,
            $"Callout Stroke sits on Save as…: stroke={strokeOrigin.X}+{thickness.ActualWidth} save={saveOrigin.X}");
        Assert.NotNull(editor.FindName("BoldBtn"));
        Assert.NotNull(editor.FindName("FontFamilyBox"));
        Assert.NotNull(editor.FindName("AlignMenuBtn"));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName((Button)editor.FindName("BoldBtn")!)));

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
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("TextStylePanel")!).Visibility);
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, ((StackPanel)editor.FindName("TextGroupPanel")!).Visibility);

        ((RadioButton)editor.FindName("ArrowToolBtn")!).IsChecked = true;
        Assert.Contains(sizeBox.Items.OfType<ComboBoxItem>(),
            item => item.Content is string s && s == "1");

        ((RadioButton)editor.FindName("FilledRectangleToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, color.Visibility);
        Assert.Equal(Visibility.Visible, thickness.Visibility);
        Assert.Equal(Visibility.Collapsed, fill.Visibility);

        ((RadioButton)editor.FindName("PixelateToolBtn")!).IsChecked = true;
        Assert.Equal(Visibility.Visible, effects.Visibility);
        Assert.Equal(Visibility.Collapsed, color.Visibility);

        typeof(EditorWindow).GetMethod("OnCropClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { editor, new RoutedEventArgs() });
        Assert.Equal(Visibility.Visible, cropRatio.Visibility);
        Assert.Equal(true, ((RadioButton)editor.FindName("PixelateToolBtn")!).IsChecked);
        ((RadioButton)editor.FindName("SelectToolBtn")!).IsChecked = true;

        editor.Width = EditorShellContract.MinimumEditorLogicalWidth;
        editor.UpdateLayout();
        var toolbar = (Border)editor.FindName("TopToolbar")!;
        Assert.True(toolbar.ActualWidth <= editor.ActualWidth);
        Assert.True(primary.ActualHeight <= 42, $"Primary tools wrapped at minimum width: {primary.ActualHeight}");

        MarqueeSelectsEveryIntersectingAnnotation(editor);
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
        var a = new System.Windows.Shapes.Rectangle { Width = 40, Height = 40, Fill = Brushes.Red };
        var b = new System.Windows.Shapes.Rectangle { Width = 40, Height = 40, Fill = Brushes.Blue };
        Canvas.SetLeft(a, 10); Canvas.SetTop(a, 10);
        Canvas.SetLeft(b, 80); Canvas.SetTop(b, 10);
        a.Tag = new AnnotationData { Type = AnnotationData.TypeRectangle };
        b.Tag = new AnnotationData { Type = AnnotationData.TypeRectangle };
        canvas.Children.Add(a);
        canvas.Children.Add(b);
        editor.UpdateLayout();

        typeof(EditorWindow).GetField("_marqueeStart",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(editor, new Point(0, 0));
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
        var zoomBox = (ComboBox)editor.FindName("ZoomBox")!;
        var zoomLabel = (TextBlock)editor.FindName("ZoomLabel")!;

        editor.Close();

        Assert.True(vw > 1 && vh > 1, $"viewport not laid out (size={vw}x{vh})");
        Assert.True(zoom > 1.05, $"tiny capture stayed at native size: zoom={zoom}, viewport={vw}x{vh}");
        Assert.True(zoom * imgW <= vw + 1 && zoom * imgH <= vh + 1,
            $"tiny capture overflowed the viewport: zoom={zoom}, scaled={zoom * imgW}x{zoom * imgH}, viewport={vw}x{vh}");
        Assert.Equal("Fit", (zoomBox.SelectedItem as ComboBoxItem)?.Content);
        Assert.Equal(Visibility.Collapsed, zoomLabel.Visibility);
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
