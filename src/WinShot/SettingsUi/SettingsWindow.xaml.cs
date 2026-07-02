using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
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
        if (OverlayAutoCloseCheck is null || OverlayCloseBox is null) return;
        OverlayCloseBox.IsEnabled = OverlayAutoCloseCheck.IsChecked == true;
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
        if (e.Key == Key.Escape) Close();
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
        SelectByTag(OverlayPositionCombo, s.OverlayPosition, fallbackIndex: 0);
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
        SelectByTag(SelfTimerCombo, s.SelfTimerSeconds.ToString(), fallbackIndex: 1);
        SelfTimerBox.Text = s.SelfTimerSeconds.ToString();
        CursorOnScreenshotsCheck.IsChecked = s.CursorOnScreenshots;
        FreezeScreenCheck.IsChecked = s.FreezeScreen;
        SelectByTag(CrosshairModeCombo, s.CrosshairMode, fallbackIndex: 1);
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

    private static int RetentionDaysToSliderIndex(int days)
    {
        // Snap to the nearest defined stop (0 stays "Never").
        int best = 0;
        int bestDelta = int.MaxValue;
        for (int i = 0; i < HistoryRetentionDayStops.Length; i++)
        {
            int delta = Math.Abs(HistoryRetentionDayStops[i] - days);
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
    }

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

            int overlaySeconds = ReadInt(OverlayCloseBox, 0, 3600, ref valid);
            int historyLimit = ReadInt(HistoryLimitBox, 1, 5000, ref valid);
            int retentionDays = ReadInt(RetentionDaysBox, 0, 3650, ref valid);
            int selfTimer = ReadInt(
                SelfTimerBox,
                SelfTimerOptions.MinDelaySeconds,
                SelfTimerOptions.MaxDelaySeconds,
                ref valid);
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
            s.PlaySounds = PlaySoundsCheck.IsChecked == true;
            s.ShutterSound = SelectedTag(ShutterSoundCombo, "default");
            s.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;

            // General > "After capture" matrix (per-action, Screenshot vs Recording columns).
            s.ScreenshotShowOverlay = ScreenshotShowOverlayCheck.IsChecked == true;
            s.RecordingShowOverlay = RecordingShowOverlayCheck.IsChecked == true;
            s.ScreenshotCopy = ScreenshotCopyCheck.IsChecked == true;
            s.RecordingCopy = RecordingCopyCheck.IsChecked == true;
            s.ScreenshotSave = ScreenshotSaveCheck.IsChecked == true;
            s.RecordingSave = RecordingSaveCheck.IsChecked == true;
            s.ScreenshotOpenAnnotate = ScreenshotOpenAnnotateCheck.IsChecked == true;
            s.ScreenshotOpenEditor = ScreenshotOpenEditorCheck.IsChecked == true;
            s.ScreenshotPin = ScreenshotPinCheck.IsChecked == true;
            s.RecordingOpenEditor = RecordingOpenEditorCheck.IsChecked == true;

            // Derive the legacy single-valued capture fields the screenshot flow still reads.
            // "Show overlay" wins (the quick-actions card); copy is applied in addition.
            s.AutoCopyToClipboard = ScreenshotCopyCheck.IsChecked == true;
            s.PostCaptureAction =
                ScreenshotShowOverlayCheck.IsChecked == true ? "overlay" :
                ScreenshotPinCheck.IsChecked == true ? "pin" :
                (ScreenshotOpenEditorCheck.IsChecked == true || ScreenshotOpenAnnotateCheck.IsChecked == true) ? "edit" :
                ScreenshotSaveCheck.IsChecked == true ? "save" :
                ScreenshotCopyCheck.IsChecked == true ? "copy" : "overlay";

            // Quick Access overlay
            s.OverlayPosition = SelectedTag(OverlayPositionCombo, "left");
            s.OverlayMoveToActiveScreen = OverlayMoveToActiveScreenCheck.IsChecked == true;
            s.OverlaySizePercent = (int)Math.Round(OverlaySizeSlider.Value);
            s.OverlayAutoClose = OverlayAutoCloseCheck.IsChecked == true;
            // Persist seconds when auto-close is on; otherwise 0 = stay until dismissed
            // (preserves the legacy meaning of OverlayAutoCloseSeconds for downstream code).
            s.OverlayAutoCloseSeconds = OverlayAutoCloseCheck.IsChecked == true ? overlaySeconds : 0;
            s.OverlayAutoCloseAction = SelectedTag(OverlayActionCombo, "save-close");
            s.OverlayCloseAfterDragging = OverlayCloseAfterDragCheck.IsChecked == true;
            s.OverlaySaveButtonBehavior = SelectedTag(OverlaySaveBehaviorCombo, "export");

            // Hotkeys (real + placeholder; see SettingsWindow.Shortcuts.cs)
            SaveShortcutBoxes(s);

            // Recording > General
            s.ShowRecordingControls = ShowRecordingControlsCheck.IsChecked == true;
            s.ShowRecordingTimer = ShowRecordingTimerCheck.IsChecked == true;
            s.ScaleHiDpiVideo = ScaleHiDpiVideoCheck.IsChecked == true;
            s.DoNotDisturbWhileRecording = DoNotDisturbCheck.IsChecked == true;
            s.CaptureCursor = CaptureCursorCheck.IsChecked == true;
            s.ShowClickHighlights = ClickHighlightsCheck.IsChecked == true;
            s.ShowKeystrokes = KeystrokesCheck.IsChecked == true;
            s.RememberLastSelection = RememberLastSelectionCheck.IsChecked == true;
            s.DimScreenWhileRecording = DimScreenCheck.IsChecked == true;
            s.ShowRecordingCountdown = ShowCountdownCheck.IsChecked == true;
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

            // Annotate
            s.InverseArrowDirection = InverseArrowCheck.IsChecked == true;
            s.SmoothPencil = SmoothPencilCheck.IsChecked == true;
            s.RememberLastTool = RememberLastToolCheck.IsChecked == true;
            s.DrawShadowOnObjects = DrawShadowCheck.IsChecked == true;
            s.AutoExpandCanvas = AutoExpandCanvasCheck.IsChecked == true;
            s.ShowColorNames = ShowColorNamesCheck.IsChecked == true;
            s.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
            s.ShowDockIcon = ShowDockIconCheck.IsChecked == true;

            // OCR (Annotate tab)
            s.OcrLanguage = SelectedTag(OcrLanguageCombo, "auto");
            s.OcrJoinLines = OcrJoinLinesCheck.IsChecked == true;
            s.OcrDetectLinks = OcrDetectLinksCheck.IsChecked == true;

            // Pinned screenshots (Annotate tab)
            s.PinnedRoundedCorners = PinnedRoundedCornersCheck.IsChecked == true;
            s.PinnedShadow = PinnedShadowCheck.IsChecked == true;
            s.PinnedBorder = PinnedBorderCheck.IsChecked == true;

            // Screenshots
            s.AddPixelBorder = AddPixelBorderCheck.IsChecked == true;
            s.ConvertToSrgb = ConvertToSrgbCheck.IsChecked == true;
            s.ScreenshotBackground = SelectedTag(BackgroundCombo, "none");
            s.CursorOnScreenshots = CursorOnScreenshotsCheck.IsChecked == true;
            s.FreezeScreen = FreezeScreenCheck.IsChecked == true;
            s.CrosshairMode = SelectedTag(CrosshairModeCombo, "command");
            s.ShowCrosshair = ShowCrosshairCheck.IsChecked == true;
            s.ShowMagnifier = ShowMagnifierCheck.IsChecked == true;
            s.SelfTimerSeconds = selfTimer;

            // Naming & history (Advanced tab)
            s.FileNameTemplate = TemplateBox.Text.Trim();
            s.AskForNameAfterCapture = AskForNameCheck.IsChecked == true;
            s.AddRetinaSuffix = AddRetinaSuffixCheck.IsChecked == true;
            s.CopyToClipboardFormat = SelectedTag(CopyFormatCombo, "file-image");
            s.AllInOneRememberLast = AllInOneRememberCheck.IsChecked == true;
            s.HistoryLimit = historyLimit;
            s.HistoryRetentionDays = retentionDays;

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
            SaveFolderBox, OverlayCloseBox, HistoryLimitBox, RetentionDaysBox, SelfTimerBox,
            RecordingFpsBox, GifFpsBox, WebcamSizeBox, CountdownBox, TemplateBox,
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

    private static string HotkeyValue(TextBox box) =>
        HotkeyManager.TryNormalizeGesture(box.Text, out string? normalized)
            ? normalized!
            : box.Text.Trim();

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
