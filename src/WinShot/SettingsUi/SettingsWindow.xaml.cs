using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Capture;
using WinShot.Core;
using WF = System.Windows.Forms;

namespace WinShot.SettingsUi;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xE8, 0x5C, 0x5C));
    private static readonly SolidColorBrush NormalBorderBrush = new(Color.FromRgb(0x55, 0x55, 0x55));

    static SettingsWindow()
    {
        ErrorBrush.Freeze();
        NormalBorderBrush.Freeze();
    }

    private readonly SettingsService _settings;
    private bool _saving;

    public SettingsWindow(SettingsService settings)
    {
        // Load the shared theme before parsing XAML (which references theme brushes), rather
        // than relying on another window having loaded it first. Idempotent.
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _settings = settings;
        BuildShortcutsTab();
        LoadFromSettings();
        ConfigureAccessibility();
        ApplyHighContrastPalette();
        PopulateAbout();
        WireInlineHotkeyConflictChecks();
        DarkTitleBar.Apply(this);
    }

    /// <summary>The section panels in tab-bar order; index matches SectionList.SelectedIndex.</summary>
    private ScrollViewer[] Sections() =>
        new[]
        {
            SectionGeneral, SectionShortcuts, SectionQuickAccess, SectionRecording,
            SectionScreenshots, SectionAnnotate, SectionAdvanced, SectionAbout,
        };

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fired once during InitializeComponent (initial SelectedIndex) and on every click.
        if (SectionList is null) return;
        var sections = Sections();
        if (sections.Any(s => s is null)) return; // tree still building

        int index = SectionList.SelectedIndex;
        if (index < 0) index = 0;
        for (int i = 0; i < sections.Length; i++)
            sections[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Switches the Recording General/Video/GIF sub-tab.</summary>
    private void OnRecordingSubChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordingSubList is null ||
            RecordingGeneralPanel is null || RecordingVideoPanel is null || RecordingGifPanel is null)
            return;

        int index = RecordingSubList.SelectedIndex;
        if (index < 0) index = 0;
        RecordingGeneralPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingVideoPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RecordingGifPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Enables the overlay auto-close interval field only when auto-close is on.</summary>
    private void OnOverlayAutoCloseToggled(object sender, RoutedEventArgs e)
    {
        if (OverlayAutoCloseCheck is null || OverlayCloseBox is null || OverlayActionCombo is null) return;
        bool enabled = OverlayAutoCloseCheck.IsChecked == true;
        OverlayCloseBox.IsEnabled = enabled;
        OverlayActionCombo.IsEnabled = enabled;
    }

    /// <summary>Maps each input box to the index of the section that contains it.</summary>
    private int SectionIndexOf(TextBox box)
    {
        var sections = Sections();
        for (int i = 0; i < sections.Length; i++)
        {
            if (IsDescendantOf(box, sections[i]))
                return i;
        }
        return 0;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Opens the settings window, or activates the instance that is already open.</summary>
    public static SettingsWindow Show(SettingsService settings)
    {
        var total = Stopwatch.StartNew();
        long createMs = 0;
        long resetMs = 0;
        long loadMs = 0;
        long centerMs = 0;
        long showMs = 0;
        long activateMs = 0;

        if (_instance is null)
        {
            var step = Stopwatch.StartNew();
            CreateInstance(settings);
            createMs = step.ElapsedMilliseconds;
        }

        var instance = _instance ?? throw new InvalidOperationException("Settings window was not created.");

        if (!instance.IsVisible)
        {
            var step = Stopwatch.StartNew();
            instance.ShowInTaskbar = true;
            instance.ResetValidation();
            resetMs = step.ElapsedMilliseconds;
            step.Restart();
            instance.LoadFromSettings();
            loadMs = step.ElapsedMilliseconds;
            step.Restart();
            instance.CenterOnWorkArea();
            centerMs = step.ElapsedMilliseconds;
            step.Restart();
            instance.Show();
            showMs = step.ElapsedMilliseconds;
        }
        else if (instance.WindowState == WindowState.Minimized)
        {
            instance.WindowState = WindowState.Normal;
        }

        var activate = Stopwatch.StartNew();
        instance.Activate();
        activateMs = activate.ElapsedMilliseconds;
        if (total.ElapsedMilliseconds > 50)
        {
            Log.Info(
                "Perf settings window breakdown: " +
                $"create={createMs} reset={resetMs} load={loadMs} center={centerMs} " +
                $"show={showMs} activate={activateMs} total={total.ElapsedMilliseconds} ms");
        }
        return instance;
    }

    /// <summary>Selects the About tab (used by the tray "About WinShot…" item).</summary>
    public void SelectAboutTab()
    {
        if (SectionList is not null)
            SectionList.SelectedIndex = Sections().Length - 1;
    }

    private static void CreateInstance(SettingsService settings)
    {
        _instance = new SettingsWindow(settings);
        _instance.Closed += (_, _) =>
        {
            _instance = null;
            MemoryCleanup.Request();
        };
    }

    private void CenterOnWorkArea()
    {
        var area = SystemParameters.WorkArea;
        double left = area.Left + (area.Width - Width) / 2;
        double top = area.Top + (area.Height - Height) / 2;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (IsVisible && hwnd != IntPtr.Zero &&
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                (int)Math.Round(left),
                (int)Math.Round(top),
                0,
                0,
                SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate))
        {
            return;
        }

        Left = left;
        Top = top;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ApplyParkedWindowStyle(parked: false);
        base.OnClosing(e);
    }

    private void ApplyParkedWindowStyle(bool parked)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GwlExStyle);
        int updated = parked ? style | WsExTransparent : style & ~WsExTransparent;
        if (updated != style)
            SetWindowLong(hwnd, GwlExStyle, updated);
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;

        // General
        SaveFolderBox.Text = s.SaveFolder;
        StartupCheck.IsChecked = s.LaunchAtStartup;
        UpdatesCheck.IsChecked = s.CheckForUpdatesOnStartup;
        HideIconsCheck.IsChecked = s.HideDesktopIconsDuringCapture;
        PlaySoundsCheck.IsChecked = s.PlaySounds;
        SelectByTag(ShutterSoundCombo, s.ShutterSound, fallbackIndex: 0);
        ShowTrayIconCheck.IsChecked = s.ShowTrayIcon;
        SelectByTag(PostCaptureActionCombo, PostCaptureAction.Normalize(s.PostCaptureAction), fallbackIndex: 0);
        PostCaptureCopyCheck.IsChecked = s.AutoCopyToClipboard;

        // General > "After capture" matrix
        ScreenshotShowOverlayCheck.IsChecked = s.ScreenshotShowOverlay;
        RecordingShowOverlayCheck.IsChecked = s.RecordingShowOverlay;
        ScreenshotCopyCheck.IsChecked = s.ScreenshotCopy;
        RecordingCopyCheck.IsChecked = s.RecordingCopy;
        ScreenshotSaveCheck.IsChecked = s.ScreenshotSave;
        RecordingSaveCheck.IsChecked = s.RecordingSave;
        ScreenshotOpenAnnotateCheck.IsChecked = s.ScreenshotOpenAnnotate;
        ScreenshotOpenEditorCheck.IsChecked = s.ScreenshotOpenEditor;
        ScreenshotPinCheck.IsChecked = s.ScreenshotPin;
        RecordingOpenEditorCheck.IsChecked = s.RecordingOpenEditor;

        // Quick Access overlay
        SelectByTag(OverlayPositionCombo, s.OverlayPosition, fallbackIndex: 5);
        OverlayMoveToActiveScreenCheck.IsChecked = s.OverlayMoveToActiveScreen;
        OverlaySizeSlider.Value = Math.Clamp(s.OverlaySizePercent, 0, 100);
        OverlayAutoCloseCheck.IsChecked = s.OverlayAutoClose;
        SelectByTag(OverlayActionCombo, s.OverlayAutoCloseAction, fallbackIndex: 0);
        // Show a usable interval even when auto-close was previously off / seconds==0.
        OverlayCloseBox.Text = (s.OverlayAutoCloseSeconds > 0 ? s.OverlayAutoCloseSeconds : 5).ToString();
        OverlayCloseBox.IsEnabled = s.OverlayAutoClose;
        OverlayActionCombo.IsEnabled = s.OverlayAutoClose;
        OverlayCloseAfterDragCheck.IsChecked = s.OverlayCloseAfterDragging;
        SelectByTag(OverlaySaveBehaviorCombo, s.OverlaySaveButtonBehavior, fallbackIndex: 0);

        // Hotkeys (Shortcuts tab is generated from the catalog; see SettingsWindow.Shortcuts.cs)
        LoadShortcutBoxes();

        // Recording > General
        ShowRecordingControlsCheck.IsChecked = s.ShowRecordingControls;
        ShowRecordingTimerCheck.IsChecked = s.ShowRecordingTimer;
        ScaleHiDpiVideoCheck.IsChecked = s.ScaleHiDpiVideo;
        DoNotDisturbCheck.IsChecked = s.DoNotDisturbWhileRecording;
        CaptureCursorCheck.IsChecked = s.CaptureCursor;
        ClickHighlightsCheck.IsChecked = s.ShowClickHighlights;
        KeystrokesCheck.IsChecked = s.ShowKeystrokes;
        RememberLastSelectionCheck.IsChecked = s.RememberLastSelection;
        DimScreenCheck.IsChecked = s.DimScreenWhileRecording;
        ShowCountdownCheck.IsChecked = s.ShowRecordingCountdown;
        CountdownBox.Text = s.RecordingCountdownSeconds.ToString();
        SelectByTag(WebcamCombo, s.WebcamOverlayPosition, fallbackIndex: 0);
        WebcamSizeBox.Text = RecordingOptions.ClampWebcamSizePercent(s.WebcamOverlaySizePercent).ToString();

        // Recording > Video
        SelectByTag(MaxResolutionCombo, s.RecordingMaxResolution, fallbackIndex: 0);
        RecordingFpsBox.Text = s.RecordingFps.ToString();
        RecordAudioCheck.IsChecked = s.RecordAudio;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        RecordAudioMonoCheck.IsChecked = s.RecordAudioMono;
        OpenVideoEditorCheck.IsChecked = s.OpenVideoEditorAfterRecording;

        // Recording > GIF
        GifFpsBox.Text = s.GifFps.ToString();
        GifQualitySlider.Value = Math.Clamp(s.GifQuality, 0, 100);
        OptimizeGifsCheck.IsChecked = s.OptimizeGifs;
        SelectByTag(GifSizeCombo, s.GifSize, fallbackIndex: 0);

        // Screenshots
        SelectByTag(FormatCombo, s.ImageFormat, fallbackIndex: 0);
        HiDpiCheck.IsChecked = s.DownscaleHiDpi;
        ConvertToSrgbCheck.IsChecked = s.ConvertToSrgb;
        AddPixelBorderCheck.IsChecked = s.AddPixelBorder;
        SelectByTag(BackgroundCombo, s.ScreenshotBackground, fallbackIndex: 0);
        SelectByTag(SelfTimerCombo, s.SelfTimerSeconds.ToString(), fallbackIndex: 0);
        SelfTimerBox.Text = s.SelfTimerSeconds.ToString();
        CursorOnScreenshotsCheck.IsChecked = s.CursorOnScreenshots;
        FreezeScreenCheck.IsChecked = s.FreezeScreen;
        SelectByTag(CrosshairModeCombo, s.ShowCrosshair ? s.CrosshairMode : "never", fallbackIndex: 1);
        ShowCrosshairCheck.IsChecked = s.ShowCrosshair;
        ShowMagnifierCheck.IsChecked = s.ShowMagnifier;

        // Annotate
        InverseArrowCheck.IsChecked = s.InverseArrowDirection;
        SmoothPencilCheck.IsChecked = s.SmoothPencil;
        RememberLastToolCheck.IsChecked = s.RememberLastTool;
        DrawShadowCheck.IsChecked = s.DrawShadowOnObjects;
        AutoExpandCanvasCheck.IsChecked = s.AutoExpandCanvas;
        ShowColorNamesCheck.IsChecked = s.ShowColorNames;
        AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
        ShowDockIconCheck.IsChecked = s.ShowDockIcon;

        // Advanced
        TemplateBox.Text = s.FileNameTemplate;
        AskForNameCheck.IsChecked = s.AskForNameAfterCapture;
        AddRetinaSuffixCheck.IsChecked = s.AddRetinaSuffix;
        SelectByTag(CopyFormatCombo, s.CopyToClipboardFormat, fallbackIndex: 0);
        PinnedRoundedCornersCheck.IsChecked = s.PinnedRoundedCorners;
        PinnedShadowCheck.IsChecked = s.PinnedShadow;
        PinnedBorderCheck.IsChecked = s.PinnedBorder;
        HistorySlider.Value = RetentionDaysToSliderIndex(s.HistoryRetentionDays);
        AllInOneRememberCheck.IsChecked = s.AllInOneRememberLast;
        SelectByTag(OcrLanguageCombo, s.OcrLanguage, fallbackIndex: 0);
        OcrJoinLinesCheck.IsChecked = s.OcrJoinLines;
        OcrDetectLinksCheck.IsChecked = s.OcrDetectLinks;
        HistoryLimitBox.Text = s.HistoryLimit.ToString();
        RetentionDaysBox.Text = s.HistoryRetentionDays.ToString();

        UpdateTemplatePreview();
    }

    // ----- Keep-history slider <-> retention-days mapping --------------------
    // Slider stops: 0 Never, 1 = 1 day, 2 = 3 days, 3 = 1 week, 4 = 1 month.
    private static readonly int[] HistoryRetentionDayStops = { 0, 1, 3, 7, 30 };

    private static int SliderIndexToRetentionDays(double index)
    {
        int i = Math.Clamp((int)Math.Round(index), 0, HistoryRetentionDayStops.Length - 1);
        return HistoryRetentionDayStops[i];
    }

    private static int RetentionDaysToSliderIndex(int days) =>
        // Snap to the nearest defined stop (0 stays "Never"); ties keep the lower index.
        Enumerable.Range(0, HistoryRetentionDayStops.Length)
            .MinBy(i => Math.Abs(HistoryRetentionDayStops[i] - days));

    /// <summary>
    /// Keep-history slider moved: mirror the snapped stop into the hidden RetentionDaysBox so
    /// OnSave's existing ReadInt(RetentionDaysBox) path picks it up. RetentionDaysBox is declared
    /// after the slider in XAML, so this can fire during InitializeComponent before it exists.
    /// </summary>
    private void OnHistorySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RetentionDaysBox is null) return;
        RetentionDaysBox.Text = SliderIndexToRetentionDays(e.NewValue).ToString();
    }

    /// <summary>
    /// CleanShot's "Reset all warning dialogs" re-enables every "Don't show again" prompt.
    /// WinShot has no suppressed-dialog store yet, so this just confirms there's nothing to
    /// restore rather than silently doing nothing (which reads as a broken button).
    /// </summary>
    private void OnResetWarnings(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "All warning dialogs are already enabled.",
            "Reset warnings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ------------------------------------------------------------ About tab

    private void PopulateAbout()
    {
        AboutVersionText.Text = $"Version {AppInfo.Version}";
        AboutVersionValue.Text = AppInfo.Version;
        AboutRuntimeValue.Text = $".NET {Environment.Version}";
        AboutOsValue.Text = Environment.OSVersion.VersionString;
        AboutInstallValue.Text = AppContext.BaseDirectory;
    }

    private async void OnAboutCheckUpdates(object sender, RoutedEventArgs e)
    {
        AboutCheckUpdatesButton.IsEnabled = false;
        string original = (string)AboutCheckUpdatesButton.Content;
        AboutCheckUpdatesButton.Content = "Checking…";
        try
        {
            var result = await UpdateService.CheckAsync();
            string message = result.State switch
            {
                UpdateState.UpdateAvailable => $"WinShot {result.LatestVersion} is available.\n\nYou have {AppInfo.Version}. Use the tray menu's \"Install update\" to update.",
                UpdateState.UpToDate => $"You're on the latest version ({AppInfo.Version}).",
                _ => $"Couldn't check for updates.\n\n{result.Message}",
            };
            MessageBox.Show(this, message, "Check for updates",
                MessageBoxButton.OK,
                result.State == UpdateState.Error ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            AboutCheckUpdatesButton.Content = original;
            AboutCheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OnAboutOpenRepo(object sender, RoutedEventArgs e) => OpenExternal(AppInfo.RepositoryUrl);

    private void OnAboutOpenLogs(object sender, RoutedEventArgs e) => OpenExternal(Log.Dir);

    private void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open '{target}' from About tab", ex);
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dialog = new WF.FolderBrowserDialog
        {
            Description = "Choose where screenshots are saved",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(SaveFolderBox.Text) ? SaveFolderBox.Text : "",
        };
        if (dialog.ShowDialog() == WF.DialogResult.OK)
            SaveFolderBox.Text = dialog.SelectedPath;
    }

    private void OnTemplateChanged(object sender, TextChangedEventArgs e) => UpdateTemplatePreview();

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e) => UpdateTemplatePreview();

    /// <summary>
    /// Renders the file name template against a throwaway copy of the current settings —
    /// FileNamer.Next increments the {n} counter, so it must never see the live instance.
    /// </summary>
    private void UpdateTemplatePreview()
    {
        if (_settings is null || TemplatePreviewText is null || TemplateBox is null) return;
        try
        {
            var preview = new SettingsService();
            preview.Current.FileNameTemplate = TemplateBox.Text;
            preview.Current.NextCounter = _settings.Current.NextCounter;
            TemplatePreviewText.Text = FileNamer.Next(preview, SelectedTag(FormatCombo, "png"));
        }
        catch (Exception ex)
        {
            Log.Error("File name template preview failed", ex);
            TemplatePreviewText.Text = "(preview unavailable)";
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = false;
        ResetValidation();
        bool valid = true;

        try
        {
            if (string.IsNullOrWhiteSpace(SaveFolderBox.Text))
            {
                MarkInvalid(SaveFolderBox, "Choose a folder to save captures into.");
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(TemplateBox.Text))
            {
                MarkInvalid(TemplateBox, "Enter a file name template, e.g. WinShot {date} at {time}.");
                valid = false;
            }

            int retentionDays = SliderIndexToRetentionDays(HistorySlider.Value);
            int selfTimer = int.Parse(SelectedTag(SelfTimerCombo, "3"));
            int overlaySeconds = 0;
            if (OverlayAutoCloseCheck.IsChecked == true)
                overlaySeconds = ReadInt(OverlayCloseBox, 1, 3600, ref valid);
            int recordingFps = ReadInt(RecordingFpsBox, 10, 60, ref valid);
            int gifFps = ReadInt(GifFpsBox, 5, 20, ref valid);
            int webcamSizePercent = ReadInt(
                WebcamSizeBox,
                RecordingOptions.MinWebcamSizePercent,
                RecordingOptions.MaxWebcamSizePercent,
                ref valid);
            int countdown = ReadInt(
                CountdownBox,
                RecordingOptions.MinCountdownSeconds,
                RecordingOptions.MaxCountdownSeconds,
                ref valid);

            var hotkeyResult = HotkeyAssignmentValidator.Validate(CreateHotkeyFields(), HotkeyAvailability.Check);
            valid &= MarkHotkeyEntryIssues(hotkeyResult);

            if (!valid)
            {
                FocusFirstInvalid();
                return;
            }

            if (!ResolveHotkeyConflicts(hotkeyResult))
            {
                FocusFirstInvalid();
                return;
            }

            var s = _settings.Current;

            // General
            s.SaveFolder = SaveFolderBox.Text.Trim();
            s.ImageFormat = SelectedTag(FormatCombo, "png");
            s.LaunchAtStartup = StartupCheck.IsChecked == true;
            s.CheckForUpdatesOnStartup = UpdatesCheck.IsChecked == true;
            s.HideDesktopIconsDuringCapture = HideIconsCheck.IsChecked == true;
            s.DownscaleHiDpi = HiDpiCheck.IsChecked == true;
            s.AutoCopyToClipboard = PostCaptureCopyCheck.IsChecked == true;
            s.PostCaptureAction = PostCaptureAction.Normalize(SelectedTag(PostCaptureActionCombo, "overlay"));

            // Recording completion actions are runtime-backed. Hidden legacy screenshot fields
            // and ambiguous save/editor flags remain untouched for migration compatibility.
            s.RecordingShowOverlay = RecordingShowOverlayCheck.IsChecked == true;
            s.RecordingCopy = RecordingCopyCheck.IsChecked == true;

            // Quick Access overlay
            s.OverlayPosition = SelectedTag(OverlayPositionCombo, "bottom-right");
            s.OverlayMoveToActiveScreen = OverlayMoveToActiveScreenCheck.IsChecked == true;
            s.OverlaySizePercent = (int)Math.Round(OverlaySizeSlider.Value);
            s.OverlayAutoClose = OverlayAutoCloseCheck.IsChecked == true;
            // Persist seconds when auto-close is on; otherwise 0 = stay until dismissed
            // (preserves the legacy meaning of OverlayAutoCloseSeconds for downstream code).
            s.OverlayAutoCloseSeconds = OverlayAutoCloseCheck.IsChecked == true ? overlaySeconds : 0;
            s.OverlayAutoCloseAction = SelectedTag(OverlayActionCombo, "save-close");
            s.OverlayCloseAfterDragging = OverlayCloseAfterDragCheck.IsChecked == true;
            s.OverlaySaveButtonBehavior = SelectedTag(OverlaySaveBehaviorCombo, "export");

            // Only executable global hotkeys are editable; unknown stored bindings survive.
            SaveShortcutBoxes(s);

            // Recording > General
            s.ShowRecordingControls = ShowRecordingControlsCheck.IsChecked == true;
            s.ShowRecordingTimer = ShowRecordingTimerCheck.IsChecked == true;
            s.ScaleHiDpiVideo = ScaleHiDpiVideoCheck.IsChecked == true;
            s.CaptureCursor = CaptureCursorCheck.IsChecked == true;
            s.ShowClickHighlights = ClickHighlightsCheck.IsChecked == true;
            s.ShowKeystrokes = KeystrokesCheck.IsChecked == true;
            s.RememberLastSelection = RememberLastSelectionCheck.IsChecked == true;
            s.DimScreenWhileRecording = DimScreenCheck.IsChecked == true;
            s.RecordingCountdownSeconds = countdown;
            s.WebcamOverlayPosition = RecordingOptions.NormalizeWebcamPosition(SelectedTag(WebcamCombo, "off"));
            s.WebcamOverlaySizePercent = RecordingOptions.ClampWebcamSizePercent(webcamSizePercent);

            // Recording > Video
            s.RecordingMaxResolution = SelectedTag(MaxResolutionCombo, "original");
            s.RecordingFps = recordingFps;
            s.RecordAudio = RecordAudioCheck.IsChecked == true;
            s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
            s.RecordAudioMono = RecordAudioMonoCheck.IsChecked == true;
            s.OpenVideoEditorAfterRecording = OpenVideoEditorCheck.IsChecked == true;

            // Recording > GIF
            s.GifFps = gifFps;
            s.GifQuality = (int)Math.Round(GifQualitySlider.Value);
            s.OptimizeGifs = OptimizeGifsCheck.IsChecked == true;
            s.GifSize = SelectedTag(GifSizeCombo, "800");

            s.OcrJoinLines = OcrJoinLinesCheck.IsChecked == true;

            // Screenshots
            s.FreezeScreen = FreezeScreenCheck.IsChecked == true;
            s.CrosshairMode = SelectedTag(CrosshairModeCombo, "command");
            s.ShowCrosshair = !string.Equals(s.CrosshairMode, "never", StringComparison.OrdinalIgnoreCase);
            s.ShowMagnifier = ShowMagnifierCheck.IsChecked == true;
            s.SelfTimerSeconds = selfTimer;

            // Naming & history (Advanced tab)
            s.FileNameTemplate = TemplateBox.Text.Trim();
            s.AllInOneRememberLast = AllInOneRememberCheck.IsChecked == true;
            s.HistoryRetentionDays = retentionDays;
            s.PinnedRoundedCorners = PinnedRoundedCornersCheck.IsChecked == true;
            s.PinnedShadow = PinnedShadowCheck.IsChecked == true;
            s.PinnedBorder = PinnedBorderCheck.IsChecked == true;

            await Task.Run(() => ApplyStartupRegistration(s.LaunchAtStartup));
            await _settings.SaveAsync();
            Close();
        }
        finally
        {
            _saving = false;
            if (IsVisible)
                SaveButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ConfigureAccessibility()
    {
        int tabIndex = 10;
        Accessible(StartupCheck, "Start WinShot at login", "Adds or removes WinShot from Windows startup.");
        Accessible(UpdatesCheck, "Check for updates at startup", "Checks GitHub Releases when WinShot starts.");
        Accessible(SaveFolderBox, "Export location", "Folder where captures are saved.");
        Accessible(BrowseSaveFolderButton, "Browse for export location", "Choose the folder where captures are saved.");
        Accessible(HideIconsCheck, "Hide desktop icons while capturing", "Temporarily hides desktop icons during capture.");
        Accessible(PostCaptureActionCombo, "After screenshot action", "Choose the action WinShot performs after a screenshot.");
        Accessible(PostCaptureCopyCheck, "Also copy screenshots", "Also copy the screenshot after actions other than Copy.");

        Accessible(OverlayPositionCombo, "Quick Access position", "Choose where the Quick Access overlay appears.");
        Accessible(OverlayMoveToActiveScreenCheck, "Move Quick Access to active screen", "Show the overlay on the screen containing the pointer.");
        Accessible(OverlaySizeSlider, "Quick Access size", "Adjust the size of the preview overlay.");
        Accessible(OverlayAutoCloseCheck, "Automatically close Quick Access", "Close the overlay after the configured interval.");
        Accessible(OverlayActionCombo, "Quick Access auto-close action", "Choose what happens when the overlay closes automatically.");
        Accessible(OverlayCloseBox, "Quick Access auto-close interval", "Enter the number of seconds before the overlay closes.");
        Accessible(OverlayCloseAfterDragCheck, "Close Quick Access after dragging", "Close the overlay after a successful drag unless Alt is held.");
        Accessible(OverlaySaveBehaviorCombo, "Quick Access save behavior", "Choose whether Save uses the export location or asks for a destination.");

        Accessible(ShowRecordingControlsCheck, "Show recording controls", "Show the control bar while recording.");
        Accessible(ShowRecordingTimerCheck, "Show recording time", "Show elapsed recording time in the control bar and tray.");
        Accessible(ScaleHiDpiVideoCheck, "Scale high DPI video", "Scale high DPI recordings down to one times size.");
        Accessible(CaptureCursorCheck, "Record cursor", "Include the mouse pointer in recordings.");
        Accessible(ClickHighlightsCheck, "Highlight recording clicks", "Show a visual highlight for mouse clicks.");
        Accessible(KeystrokesCheck, "Show recording keystrokes", "Show pressed keys in recordings.");
        Accessible(RememberLastSelectionCheck, "Remember recording area", "Reuse the last valid recording rectangle.");
        Accessible(DimScreenCheck, "Dim outside recording area", "Dim the screen outside the selected recording rectangle.");
        Accessible(CountdownBox, "Recording countdown seconds", "Use zero to start immediately.");
        Accessible(WebcamCombo, "Webcam overlay position", "Choose the webcam overlay position or turn it off.");
        Accessible(WebcamSizeBox, "Webcam overlay size", "Enter a value from 10 to 45 percent.");
        Accessible(RecordingShowOverlayCheck, "Show Quick Access after recording", "Show the local completion overlay after a recording finishes.");
        Accessible(RecordingCopyCheck, "Copy recording after capture", "Copy the completed recording file to the clipboard.");
        Accessible(MaxResolutionCombo, "Maximum video resolution", "Choose the largest output resolution for MP4 recordings.");
        Accessible(RecordingFpsBox, "Video frames per second", "Enter a value from 10 to 60.");
        Accessible(RecordAudioCheck, "Record microphone", "Capture microphone audio in MP4 recordings.");
        Accessible(SystemAudioCheck, "Record system audio", "Capture Windows system audio in MP4 recordings.");
        Accessible(RecordAudioMonoCheck, "Record microphone in mono", "Use one microphone audio channel when microphone capture is enabled.");
        Accessible(OpenVideoEditorCheck, "Open video editor after recording", "Open the local editor after an MP4 recording finishes.");
        Accessible(GifFpsBox, "GIF frames per second", "Enter a value from 5 to 20.");
        Accessible(GifQualitySlider, "GIF quality", "Adjust GIF palette quality and file size.");
        Accessible(OptimizeGifsCheck, "Optimize GIFs", "Use the selected palette quality while encoding locally.");
        Accessible(GifSizeCombo, "Maximum GIF width", "Choose the largest GIF width; smaller recordings are not enlarged.");

        Accessible(FormatCombo, "Screenshot file format", "Choose PNG, JPG, or WEBP.");
        Accessible(HiDpiCheck, "Scale high DPI screenshots", "Scale high DPI screenshots down to one times size.");
        Accessible(SelfTimerCombo, "Self timer interval", "Choose how long the self timer waits before capture.");
        Accessible(FreezeScreenCheck, "Freeze screen during selection", "Use a still desktop image while choosing a region.");
        Accessible(CrosshairModeCombo, "Crosshair mode", "Choose when selector crosshair guides appear.");
        Accessible(ShowMagnifierCheck, "Show selector magnifier", "Show the pixel magnifier while selecting a region.");

        Accessible(TemplateBox, "Capture file name template", "Use date, time, counter, application, or title tokens.");
        Accessible(HistorySlider, "Capture history retention", "Choose how long local capture history is kept.");
        Accessible(PinnedRoundedCornersCheck, "Rounded pinned screenshot corners", "Use rounded corners for pinned screenshots.");
        Accessible(PinnedShadowCheck, "Pinned screenshot shadow", "Show a shadow around pinned screenshots.");
        Accessible(PinnedBorderCheck, "Pinned screenshot border", "Show a border around pinned screenshots.");
        Accessible(AllInOneRememberCheck, "Remember All-In-One selection", "Restore the last All-In-One selection.");
        Accessible(OcrJoinLinesCheck, "Join recognized text lines", "Copy recognized text as joined lines instead of preserving line breaks.");

        foreach (Control control in new Control[]
        {
            StartupCheck, UpdatesCheck, SaveFolderBox, BrowseSaveFolderButton, HideIconsCheck,
            PostCaptureActionCombo, PostCaptureCopyCheck, OverlayPositionCombo, OverlayMoveToActiveScreenCheck,
            OverlaySizeSlider, OverlayAutoCloseCheck, OverlayActionCombo, OverlayCloseBox,
            OverlayCloseAfterDragCheck, OverlaySaveBehaviorCombo, ShowRecordingControlsCheck,
            ShowRecordingTimerCheck, ScaleHiDpiVideoCheck, CaptureCursorCheck, ClickHighlightsCheck,
            KeystrokesCheck, RememberLastSelectionCheck, DimScreenCheck, CountdownBox, WebcamCombo,
            WebcamSizeBox, RecordingShowOverlayCheck, RecordingCopyCheck, MaxResolutionCombo,
            RecordingFpsBox, RecordAudioCheck, SystemAudioCheck, RecordAudioMonoCheck,
            OpenVideoEditorCheck, GifFpsBox, GifQualitySlider, OptimizeGifsCheck, GifSizeCombo,
            FormatCombo, HiDpiCheck, SelfTimerCombo,
            FreezeScreenCheck, CrosshairModeCombo, ShowMagnifierCheck, TemplateBox, HistorySlider,
            PinnedRoundedCornersCheck, PinnedShadowCheck, PinnedBorderCheck, AllInOneRememberCheck,
            OcrJoinLinesCheck,
        })
        {
            control.TabIndex = tabIndex++;
        }

        CancelButton.TabIndex = 1000;
        SaveButton.TabIndex = 1001;
    }

    private static void Accessible(Control control, string name, string helpText)
    {
        AutomationProperties.SetName(control, name);
        AutomationProperties.SetHelpText(control, helpText);
    }

    /// <summary>
    /// WPF does not automatically replace custom application brushes in High Contrast mode.
    /// Apply system colors to this window only; shared theme resources and other windows remain
    /// untouched. The logical tree covers collapsed pages too, so changing categories later is safe.
    /// </summary>
    private void ApplyHighContrastPalette()
    {
        if (!SystemParameters.HighContrast)
            return;

        Background = SystemColors.WindowBrush;
        foreach (DependencyObject node in LogicalDescendants(this))
        {
            switch (node)
            {
                case TextBlock text:
                    text.Foreground = SystemColors.WindowTextBrush;
                    break;
                case Control control:
                    control.Foreground = SystemColors.WindowTextBrush;
                    control.BorderBrush = SystemColors.WindowTextBrush;
                    break;
                case Border border:
                    border.Background = SystemColors.WindowBrush;
                    border.BorderBrush = SystemColors.WindowTextBrush;
                    break;
                case System.Windows.Shapes.Rectangle rectangle:
                    rectangle.Fill = SystemColors.WindowTextBrush;
                    break;
            }
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
                continue;
            yield return dependencyObject;
            foreach (DependencyObject descendant in LogicalDescendants(dependencyObject))
                yield return descendant;
        }
    }

    /// <summary>
    /// Lightweight in-app feedback: after a hotkey field loses focus, flag any field that
    /// now duplicates another field's gesture. Purely cosmetic — the authoritative
    /// validation (including app-ownership probing) still runs in OnSave and is untouched.
    /// </summary>
    private void WireInlineHotkeyConflictChecks()
    {
        foreach (var box in RealHotkeyBoxes())
            box.LostKeyboardFocus += (_, _) => RefreshInlineHotkeyConflicts();
    }

    private void RefreshInlineHotkeyConflicts()
    {
        var boxes = RealHotkeyBoxes();

        // Group boxes by their normalized gesture; any group with >1 non-empty member conflicts.
        var byGesture = new Dictionary<string, List<HotkeyBox>>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in boxes)
        {
            string gesture = NormalizeForCompare(box.Text);
            if (gesture.Length == 0) continue;
            if (!byGesture.TryGetValue(gesture, out var list))
                byGesture[gesture] = list = new List<HotkeyBox>();
            list.Add(box);
        }

        foreach (var box in boxes)
        {
            // Don't disturb a field that OnSave already flagged with a tooltip/error this pass,
            // and don't fight the focus visual on the box currently being edited.
            string gesture = NormalizeForCompare(box.Text);
            bool conflict = gesture.Length > 0 &&
                            byGesture.TryGetValue(gesture, out var list) && list.Count > 1;

            if (conflict)
            {
                box.BorderBrush = ErrorBrush;
                box.ToolTip = $"{box.Text} is already assigned to another action.";
            }
            else if (ReferenceEquals(box.BorderBrush, ErrorBrush))
            {
                // Only clear marks we own (an error border with our conflict tooltip).
                box.BorderBrush = NormalBorderBrush;
                box.ToolTip = null;
            }
        }
    }

    private static string NormalizeForCompare(string text) =>
        HotkeyManager.TryNormalizeGesture(text, out string? normalized)
            ? normalized!
            : text.Trim();

    private TextBox[] AllInputBoxes() =>
        new TextBox[]
        {
            SaveFolderBox, RecordingFpsBox, GifFpsBox, WebcamSizeBox, CountdownBox, TemplateBox,
        }.Concat(RealHotkeyBoxes()).ToArray();

    private void ResetValidation()
    {
        foreach (var box in AllInputBoxes())
        {
            box.BorderBrush = NormalBorderBrush;
            box.ToolTip = null;
        }
    }

    /// <summary>Switches to the section containing the first invalid box so the error is visible.</summary>
    private void FocusFirstInvalid()
    {
        foreach (var box in AllInputBoxes())
        {
            if (!ReferenceEquals(box.BorderBrush, ErrorBrush)) continue;
            if (SectionList is not null)
                SectionList.SelectedIndex = SectionIndexOf(box);
            RevealRecordingSubTabFor(box);
            box.Focus();
            return;
        }
    }

    /// <summary>If an invalid box lives in a hidden Recording sub-panel, switch to its sub-tab.</summary>
    private void RevealRecordingSubTabFor(TextBox box)
    {
        if (RecordingSubList is null) return;
        if (RecordingVideoPanel is not null && IsDescendantOf(box, RecordingVideoPanel))
            RecordingSubList.SelectedIndex = 1;
        else if (RecordingGifPanel is not null && IsDescendantOf(box, RecordingGifPanel))
            RecordingSubList.SelectedIndex = 2;
        else if (RecordingGeneralPanel is not null && IsDescendantOf(box, RecordingGeneralPanel))
            RecordingSubList.SelectedIndex = 0;
    }

    private static void MarkInvalid(TextBox box, string message)
    {
        box.BorderBrush = ErrorBrush;
        box.ToolTip = message;
    }

    /// <summary>Parses an int; unparseable input blocks the save, out-of-range input is clamped.</summary>
    private static int ReadInt(TextBox box, int min, int max, ref bool valid)
    {
        if (!int.TryParse(box.Text.Trim(), out int value))
        {
            MarkInvalid(box, $"Enter a number between {min} and {max}.");
            valid = false;
            return min;
        }
        int clamped = Math.Clamp(value, min, max);
        if (clamped != value)
            box.Text = clamped.ToString();
        return clamped;
    }

    private static bool MarkHotkeyEntryIssues(HotkeyAssignmentValidator.Result result)
    {
        bool valid = true;
        foreach (var issue in result.Issues.Where(issue => issue.Kind != HotkeyAssignmentIssueKind.UsedByAnotherApp))
        {
            foreach (var box in issue.Boxes)
                MarkInvalid(box, issue.Message);
            valid = false;
        }
        return valid;
    }

    private bool ResolveHotkeyConflicts(HotkeyAssignmentValidator.Result result)
    {
        var issue = result.Issues.FirstOrDefault(issue => issue.Kind == HotkeyAssignmentIssueKind.UsedByAnotherApp);
        if (issue is null)
            return true;

        var source = HotkeyConflictInspector.DescribeConflict(issue.Gesture);
        string actionLabel = issue.Labels.FirstOrDefault() ?? "This action";
        HotkeyConflictChoice choice = HotkeyConflictDialog.Show(this, actionLabel, issue.Gesture, source);

        if (choice == HotkeyConflictChoice.FindApp)
        {
            var probe = HotkeyOwnerProbeDialog.Show(this, issue.Gesture);
            string message = probe.Found
                ? $"{probe.Source.DisplayName} appears to catch {issue.Gesture}. Choose a different WinShot hotkey or change it there."
                : "WinShot could not identify the app. Choose a different WinShot hotkey or close likely hotkey apps and try again.";
            foreach (var box in issue.Boxes)
                MarkInvalid(box, message);
        }
        else if (choice == HotkeyConflictChoice.Change)
        {
            OpenConflictSource(source);
            foreach (var box in issue.Boxes)
                MarkInvalid(box, $"{issue.Gesture} is still assigned in {source.DisplayName}. Change it there, then save again.");
        }
        else
        {
            foreach (var box in issue.Boxes)
                MarkInvalid(box, $"{source.DisplayName} keeps {issue.Gesture}. Choose a different WinShot hotkey.");
        }

        return false;
    }

    private static void OpenConflictSource(HotkeyConflictSource source)
    {
        string target = string.IsNullOrWhiteSpace(source.LaunchTarget)
            ? "ms-settings:keyboard"
            : source.LaunchTarget;

        if (TryStart(target))
            return;

        TryStart("ms-settings:keyboard");
    }

    private static bool TryStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open '{target}' for hotkey conflict", ex);
            return false;
        }
    }

    private static void SelectByTag(ComboBox combo, string value, int fallbackIndex)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = fallbackIndex;
    }

    private static string SelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    // Delegates to the shared helper so Settings and the boot-time self-heal write the
    // identical Run-key value (see WinShot.Core.StartupRegistration).
    private static void ApplyStartupRegistration(bool enabled)
        => StartupRegistration.Apply(enabled);

    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
