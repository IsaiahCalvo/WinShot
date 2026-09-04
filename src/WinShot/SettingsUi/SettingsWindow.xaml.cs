using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    /// <summary>The theme's error red (single shared instance, so ReferenceEquals
    /// identifies borders we set). Restores go through ClearValue so the style's
    /// hairline BorderBrush resumes instead of a hard-coded gray.</summary>
    private static Brush ErrorBrush => (Brush)Application.Current.FindResource("TextErrorBrush");

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
        WireLiveApply();
        UpdateTitleForSection(SectionList?.SelectedIndex ?? 0);
        // Mica: the cards already supply their own opaque surface colour, so contrast is
        // unchanged and only the gutter between them picks up what is behind the window.
        DarkTitleBar.ApplyMica(this);
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
        CrossFade(sections, index);
        UpdateTitleForSection(index);
        if (index == sections.Length - 1)
            LoadReleaseNotesAsync();
    }

    /// <summary>Switches the Recording General/Video/GIF sub-tab.</summary>
    private void OnRecordingSubChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordingSubList is null ||
            RecordingGeneralPanel is null || RecordingVideoPanel is null || RecordingGifPanel is null)
            return;

        int index = RecordingSubList.SelectedIndex;
        if (index < 0) index = 0;
        CrossFade([RecordingGeneralPanel, RecordingVideoPanel, RecordingGifPanel], index);
    }

    // ------------------------------------------------------------------ motion

    /// <summary>True when Windows says animation is off (Ease of Access, or a remote
    /// session). Every transition below collapses to its end state instead.</summary>
    private static bool AnimationsOff => !Motion.AnimationsEnabled();

    /// <summary>
    /// Hard-swapping eight ScrollViewers is the most visible instant change in the app.
    /// The outgoing pane fades out quick and the incoming one fades in over the swap step,
    /// so the two briefly double-expose the way CleanShot's panes do. The window height
    /// stays fixed and each pane scrolls — a deliberate difference, and the reason only
    /// opacity moves here.
    /// </summary>
    private void CrossFade(IReadOnlyList<UIElement> panes, int index)
    {
        for (int i = 0; i < panes.Count; i++)
        {
            UIElement pane = panes[i];
            if (i == index)
            {
                bool wasHidden = pane.Visibility != Visibility.Visible;
                pane.Visibility = Visibility.Visible;
                if (AnimationsOff || !IsVisible)
                {
                    pane.BeginAnimation(OpacityProperty, null);
                    pane.Opacity = 1;
                    continue;
                }
                if (wasHidden)
                    pane.Opacity = 0;
                pane.BeginAnimation(OpacityProperty, FadeTo(1, "MotionSwap"));
            }
            else if (pane.Visibility == Visibility.Visible)
            {
                if (AnimationsOff || !IsVisible)
                {
                    pane.BeginAnimation(OpacityProperty, null);
                    pane.Visibility = Visibility.Collapsed;
                    pane.Opacity = 1;
                    continue;
                }
                UIElement outgoing = pane;
                DoubleAnimation fade = FadeTo(0, "MotionQuick");
                fade.Completed += (_, _) =>
                {
                    // A later swap may already have brought this pane back.
                    if (outgoing.Opacity <= 0.01)
                    {
                        outgoing.BeginAnimation(OpacityProperty, null);
                        outgoing.Visibility = Visibility.Collapsed;
                        outgoing.Opacity = 1;
                    }
                };
                outgoing.BeginAnimation(OpacityProperty, fade);
            }
        }
    }

    private DoubleAnimation FadeTo(double to, string durationKey) => new(to, (Duration)FindResource(durationKey))
    {
        EasingFunction = (IEasingFunction)FindResource("EaseOutCubic"),
        FillBehavior = FillBehavior.HoldEnd,
    };

    /// <summary>The window scales up from 92% and fades in — the entrance every other piece
    /// of arriving chrome in the app now uses. Runs on Show(), not on re-activation.</summary>
    private void PlayEntrance()
    {
        if (ShellRoot is null || ShellScale is null)
            return;
        if (AnimationsOff)
        {
            ShellRoot.Opacity = 1;
            ShellScale.ScaleX = ShellScale.ScaleY = 1;
            return;
        }

        var duration = (Duration)FindResource("MotionEnter");
        var ease = (IEasingFunction)FindResource("EaseOutCubic");

        ShellRoot.Opacity = 0;
        ShellScale.ScaleX = ShellScale.ScaleY = 0.92;
        ShellRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        var grow = new DoubleAnimation(1, duration) { EasingFunction = ease };
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>The title names the pane you are on, the way the tab bar already does, so
    /// Alt-Tab and the taskbar preview carry the same answer the window does.</summary>
    private void UpdateTitleForSection(int index)
    {
        if (SectionList is null || index < 0 || index >= SectionList.Items.Count)
            return;
        // Each nav tab is an icon glyph above its label, so the LAST TextBlock is the
        // readable half — taking the first would put a Segoe private-use codepoint in
        // the title bar.
        string? label = SectionList.Items[index] is ListBoxItem item
            ? LogicalDescendants(item).OfType<TextBlock>().LastOrDefault()?.Text
            : null;
        Title = string.IsNullOrWhiteSpace(label) ? "WinShot settings" : $"{label} - WinShot settings";
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
            instance.PlayEntrance();
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

        // WorkArea is in DIPs, so a 720-DIP window is taller than a 1080p work area at 150%
        // scaling and centring pushes the title bar off the top of the screen. Shrink to fit
        // first — the sections scroll anyway — then clamp so the window always starts on-screen.
        if (Height > area.Height)
            Height = Math.Max(MinHeight, area.Height);
        if (Width > area.Width)
            Width = Math.Max(MinWidth, area.Width);

        double left = Math.Max(area.Left, area.Left + (area.Width - Width) / 2);
        double top = Math.Max(area.Top, area.Top + (area.Height - Height) / 2);

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
            // Toggles applied as they were changed; only typed fields can be lost, and
            // Escape used to lose them silently.
            if (HasUncommittedFieldEdits() &&
                !ThemedMessageDialog.Confirm(
                    this,
                    "Discard changes?",
                    "The fields you typed have not been applied yet. Close Settings and lose them?",
                    confirmLabel: "Discard"))
            {
                return;
            }
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

    private void LoadFromSettings() => LoadFromSettings(_settings.Current);

    /// <summary>Fills every control from a settings snapshot. Split per tab so
    /// "Restore defaults" can reload one tab from a pristine <see cref="Settings"/>
    /// without touching the others or the user's saved file.</summary>
    private void LoadFromSettings(Settings s)
    {
        using (SuspendLiveApply())
        {
            for (int i = 0; i < Sections().Length; i++)
                LoadSection(i, s);
        }
        UpdateTemplatePreview();
    }

    /// <summary>Loads one tab's controls from <paramref name="s"/>. Index matches
    /// <see cref="Sections"/> and the tab bar.</summary>
    private void LoadSection(int index, Settings s)
    {
        using var _ = SuspendLiveApply();
        switch (index)
        {
            case 0: LoadGeneral(s); break;
            case 1: LoadShortcutBoxes(s); break;
            case 2: LoadQuickAccess(s); break;
            case 3: LoadRecording(s); break;
            case 4: LoadScreenshots(s); break;
            case 5: LoadAnnotate(s); break;
            case 6: LoadAdvanced(s); break;
            // 7 = About: nothing to restore.
        }
        UpdateTemplatePreview();
    }

    private void LoadGeneral(Settings s)
    {
        SaveFolderBox.Text = s.SaveFolder;
        StartupCheck.IsChecked = s.LaunchAtStartup;
        UpdatesCheck.IsChecked = s.CheckForUpdatesOnStartup;
        HideIconsCheck.IsChecked = s.HideDesktopIconsDuringCapture;
        PlaySoundsCheck.IsChecked = s.PlaySounds;
        SelectByTag(ShutterSoundCombo, CaptureSound.Normalize(s.ShutterSound), fallbackIndex: 0);
        ShowTrayIconCheck.IsChecked = s.ShowTrayIcon;

        // "After capture" matrix — a set of independent actions, not a choice.
        ScreenshotShowOverlayCheck.IsChecked = s.ScreenshotShowOverlay;
        RecordingShowOverlayCheck.IsChecked = s.RecordingShowOverlay;
        ScreenshotCopyCheck.IsChecked = s.ScreenshotCopy;
        RecordingCopyCheck.IsChecked = s.RecordingCopy;
        ScreenshotSaveCheck.IsChecked = s.ScreenshotSave;
        ScreenshotOpenAnnotateCheck.IsChecked = s.ScreenshotOpenAnnotate;
        ScreenshotPinCheck.IsChecked = s.ScreenshotPin;
        RecordingOpenEditorCheck.IsChecked = s.RecordingOpenEditor;
    }

    private void LoadQuickAccess(Settings s)
    {
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
    }

    private void LoadRecording(Settings s)
    {
        // General
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

        // Video
        SelectByTag(MaxResolutionCombo, s.RecordingMaxResolution, fallbackIndex: 0);
        RecordingFpsBox.Text = s.RecordingFps.ToString();
        RecordAudioCheck.IsChecked = s.RecordAudio;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        RecordAudioMonoCheck.IsChecked = s.RecordAudioMono;

        // GIF
        GifFpsBox.Text = s.GifFps.ToString();
        GifQualitySlider.Value = Math.Clamp(s.GifQuality, 0, 100);
        OptimizeGifsCheck.IsChecked = s.OptimizeGifs;
        SelectByTag(GifSizeCombo, s.GifSize, fallbackIndex: 0);
    }

    private void LoadScreenshots(Settings s)
    {
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
    }

    private void LoadAnnotate(Settings s)
    {
        InverseArrowCheck.IsChecked = s.InverseArrowDirection;
        SmoothPencilCheck.IsChecked = s.SmoothPencil;
        RememberLastToolCheck.IsChecked = s.RememberLastTool;
        DrawShadowCheck.IsChecked = s.DrawShadowOnObjects;
        AutoExpandCanvasCheck.IsChecked = s.AutoExpandCanvas;
        ShowColorNamesCheck.IsChecked = s.ShowColorNames;
        AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
        ShowDockIconCheck.IsChecked = s.ShowDockIcon;
    }

    private void LoadAdvanced(Settings s)
    {
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
        ThemedMessageDialog.Show(this, "Reset warnings", "All warning dialogs are already enabled.");
    }

    // ------------------------------------------------------------ About tab

    private void PopulateAbout()
    {
        AboutIconImage.Source = AppIcon.Large();
        AboutVersionText.Text = $"Version {AppInfo.Version}";
        AboutVersionValue.Text = AppInfo.Version;
        AboutRuntimeValue.Text = $".NET {Environment.Version}";
        AboutOsValue.Text = Environment.OSVersion.VersionString;
        AboutInstallValue.Text = AppContext.BaseDirectory;
        AboutNoticesText.Text = ThirdPartyNotices.Read();
    }

    /// <summary>Fetches the latest release body for "What's new". Never blocks opening the
    /// pane, and says so plainly when GitHub cannot be reached.</summary>
    private async void LoadReleaseNotesAsync()
    {
        if (_releaseNotesRequested) return;
        _releaseNotesRequested = true;
        try
        {
            var result = await UpdateService.CheckAsync();
            string? notes = result.ReleaseNotes?.Trim();
            if (string.IsNullOrWhiteSpace(notes))
            {
                AboutReleaseNotesStatus.Text = result.State == UpdateState.Error
                    ? "Release notes are unavailable right now."
                    : "This release published no notes.";
                return;
            }

            AboutReleaseNotesStatus.Text = result.State == UpdateState.UpdateAvailable
                ? $"WinShot {result.LatestVersion} is available."
                : $"WinShot {result.LatestVersion ?? AppInfo.Version}";
            AboutReleaseNotesText.Text = notes;
            Reveal(AboutReleaseNotesScroll);
        }
        catch (Exception ex)
        {
            Log.Error("Release notes could not be loaded", ex);
            AboutReleaseNotesStatus.Text = "Release notes are unavailable right now.";
        }
    }

    private bool _releaseNotesRequested;

    private void OnDiagnosticsToggled(object sender, RoutedEventArgs e) =>
        ToggleDisclosure(AboutDiagnosticsToggle, AboutDiagnosticsPanel);

    private void OnNoticesToggled(object sender, RoutedEventArgs e) =>
        ToggleDisclosure(AboutNoticesToggle, AboutNoticesPanel);

    private void ToggleDisclosure(ToggleButton toggle, UIElement panel)
    {
        if (toggle.IsChecked == true)
            Reveal(panel);
        else
            panel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows a block with the same fade the tab swap uses, so revealed content
    /// arrives rather than appearing.</summary>
    private void Reveal(UIElement element)
    {
        element.Visibility = Visibility.Visible;
        if (AnimationsOff)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, FadeTo(1, "MotionSwap"));
    }

    private void OnAboutOpenLicenses(object sender, RoutedEventArgs e)
    {
        string dir = ThirdPartyNotices.LicensesDirectory;
        OpenExternal(Directory.Exists(dir) ? dir : AppContext.BaseDirectory);
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
            ThemedMessageDialog.Show(this, "Check for updates", message);
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

            // Everything a checkbox, combo or slider owns. Those already applied
            // themselves as they were changed; re-running the writer is idempotent and
            // keeps one description of "what the toggles mean".
            WriteToggleSettings(s);

            // Validated text fields — the reason this window still has a commit point.
            s.SaveFolder = SaveFolderBox.Text.Trim();
            s.FileNameTemplate = TemplateBox.Text.Trim();
            s.RecordingFps = recordingFps;
            s.GifFps = gifFps;
            s.RecordingCountdownSeconds = countdown;
            s.WebcamOverlaySizePercent = RecordingOptions.ClampWebcamSizePercent(webcamSizePercent);
            // Seconds only mean anything while auto-close is on; 0 keeps the legacy
            // "stay until dismissed" meaning for everything downstream.
            s.OverlayAutoCloseSeconds = OverlayAutoCloseCheck.IsChecked == true ? overlaySeconds : 0;

            // Only executable global hotkeys are editable; unknown stored bindings survive.
            SaveShortcutBoxes(s);

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

    /// <summary>
    /// Every setting a checkbox, combo or slider owns. These apply the moment they are
    /// changed (see <see cref="WireLiveApply"/>) — no OK button, which is where the modal
    /// feel actually bit. Text fields that need validating still wait for Done.
    /// </summary>
    private void WriteToggleSettings(Settings s)
    {
        // General
        s.LaunchAtStartup = StartupCheck.IsChecked == true;
        s.CheckForUpdatesOnStartup = UpdatesCheck.IsChecked == true;
        s.HideDesktopIconsDuringCapture = HideIconsCheck.IsChecked == true;
        s.PlaySounds = PlaySoundsCheck.IsChecked == true;
        s.ShutterSound = CaptureSound.Normalize(SelectedTag(ShutterSoundCombo, CaptureSound.Default));
        s.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;

        // After-capture matrix. Every ticked cell runs; none excludes another.
        s.ScreenshotShowOverlay = ScreenshotShowOverlayCheck.IsChecked == true;
        s.ScreenshotCopy = ScreenshotCopyCheck.IsChecked == true;
        s.ScreenshotSave = ScreenshotSaveCheck.IsChecked == true;
        s.ScreenshotOpenAnnotate = ScreenshotOpenAnnotateCheck.IsChecked == true;
        s.ScreenshotPin = ScreenshotPinCheck.IsChecked == true;
        s.RecordingShowOverlay = RecordingShowOverlayCheck.IsChecked == true;
        s.RecordingCopy = RecordingCopyCheck.IsChecked == true;
        s.RecordingOpenEditor = RecordingOpenEditorCheck.IsChecked == true;

        // Quick Access overlay
        s.OverlayPosition = SelectedTag(OverlayPositionCombo, "bottom-right");
        s.OverlayMoveToActiveScreen = OverlayMoveToActiveScreenCheck.IsChecked == true;
        s.OverlaySizePercent = (int)Math.Round(OverlaySizeSlider.Value);
        s.OverlayAutoClose = OverlayAutoCloseCheck.IsChecked == true;
        s.OverlayAutoCloseAction = SelectedTag(OverlayActionCombo, "save-close");
        s.OverlayCloseAfterDragging = OverlayCloseAfterDragCheck.IsChecked == true;
        s.OverlaySaveButtonBehavior = SelectedTag(OverlaySaveBehaviorCombo, "export");
        // Turning auto-close ON live would otherwise leave seconds at 0, which downstream
        // reads as "stay until dismissed" — the toggle would look broken until Done.
        if (s.OverlayAutoClose && s.OverlayAutoCloseSeconds <= 0 &&
            int.TryParse(OverlayCloseBox.Text.Trim(), out int liveSeconds))
        {
            s.OverlayAutoCloseSeconds = Math.Clamp(liveSeconds, 1, 3600);
        }

        // Recording
        s.ShowRecordingControls = ShowRecordingControlsCheck.IsChecked == true;
        s.ShowRecordingTimer = ShowRecordingTimerCheck.IsChecked == true;
        s.ScaleHiDpiVideo = ScaleHiDpiVideoCheck.IsChecked == true;
        s.CaptureCursor = CaptureCursorCheck.IsChecked == true;
        s.ShowClickHighlights = ClickHighlightsCheck.IsChecked == true;
        s.ShowKeystrokes = KeystrokesCheck.IsChecked == true;
        s.RememberLastSelection = RememberLastSelectionCheck.IsChecked == true;
        s.DimScreenWhileRecording = DimScreenCheck.IsChecked == true;
        s.WebcamOverlayPosition = RecordingOptions.NormalizeWebcamPosition(SelectedTag(WebcamCombo, "off"));
        s.RecordingMaxResolution = SelectedTag(MaxResolutionCombo, "original");
        s.RecordAudio = RecordAudioCheck.IsChecked == true;
        s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        s.RecordAudioMono = RecordAudioMonoCheck.IsChecked == true;
        s.GifQuality = (int)Math.Round(GifQualitySlider.Value);
        s.OptimizeGifs = OptimizeGifsCheck.IsChecked == true;
        s.GifSize = SelectedTag(GifSizeCombo, "800");

        // Screenshots
        s.ImageFormat = SelectedTag(FormatCombo, "png");
        s.DownscaleHiDpi = HiDpiCheck.IsChecked == true;
        s.FreezeScreen = FreezeScreenCheck.IsChecked == true;
        s.CrosshairMode = SelectedTag(CrosshairModeCombo, "command");
        s.ShowCrosshair = !string.Equals(s.CrosshairMode, "never", StringComparison.OrdinalIgnoreCase);
        s.ShowMagnifier = ShowMagnifierCheck.IsChecked == true;
        s.SelfTimerSeconds = int.TryParse(SelectedTag(SelfTimerCombo, "3"), out int timer) ? timer : 3;

        // Advanced
        s.AllInOneRememberLast = AllInOneRememberCheck.IsChecked == true;
        s.HistoryRetentionDays = SliderIndexToRetentionDays(HistorySlider.Value);
        s.PinnedRoundedCorners = PinnedRoundedCornersCheck.IsChecked == true;
        s.PinnedShadow = PinnedShadowCheck.IsChecked == true;
        s.PinnedBorder = PinnedBorderCheck.IsChecked == true;
        s.OcrJoinLines = OcrJoinLinesCheck.IsChecked == true;
    }

    // ------------------------------------------------------- live apply

    private int _liveApplySuspended;
    private DispatcherTimer? _liveApplyTimer;
    private bool _startupRegistrationDirty;

    /// <summary>Blocks live-apply while code (not the user) is moving controls.</summary>
    private IDisposable SuspendLiveApply()
    {
        _liveApplySuspended++;
        return new Suspender(this);
    }

    private sealed class Suspender(SettingsWindow owner) : IDisposable
    {
        public void Dispose() => owner._liveApplySuspended--;
    }

    /// <summary>
    /// Checkboxes, combos and sliders apply as you change them. The alternative — the whole
    /// window waiting on Done — is the modal feel the audit named, and Done still exists for
    /// the text fields that genuinely need validating (folder, template, hotkeys, the
    /// numeric bounds), where live-applying half-typed input would be worse than modal.
    /// </summary>
    private void WireLiveApply()
    {
        foreach (DependencyObject node in LogicalDescendants(this))
        {
            switch (node)
            {
                case CheckBox check:
                    check.Checked += OnLiveApplyChanged;
                    check.Unchecked += OnLiveApplyChanged;
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += OnLiveApplyChanged;
                    break;
                case Slider slider:
                    slider.ValueChanged += OnLiveApplyChanged;
                    break;
            }
        }
    }

    private void OnLiveApplyChanged(object sender, RoutedEventArgs e)
    {
        if (_liveApplySuspended > 0 || _saving)
            return;
        if (sender is CheckBox startup && ReferenceEquals(startup, StartupCheck))
            _startupRegistrationDirty = true;

        // Coalesce: dragging a slider raises ValueChanged per pixel, and each save is a
        // serialize plus two backup copies.
        _liveApplyTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnLiveApplyDue, Dispatcher);
        _liveApplyTimer.Stop();
        _liveApplyTimer.Start();
    }

    private void OnLiveApplyDue(object? sender, EventArgs e)
    {
        _liveApplyTimer?.Stop();
        if (_saving)
            return;
        try
        {
            WriteToggleSettings(_settings.Current);
            _settings.Save();
            if (_startupRegistrationDirty)
            {
                _startupRegistrationDirty = false;
                bool enabled = _settings.Current.LaunchAtStartup;
                _ = Task.Run(() => ApplyStartupRegistration(enabled));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Live-applying a settings toggle failed", ex);
        }
    }

    // ------------------------------------------------------- restore defaults

    /// <summary>Resets the tab you are looking at — not the whole app — because that is the
    /// scope the button sits in and the only one whose damage a user can see and undo.</summary>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        int index = SectionList?.SelectedIndex ?? 0;
        string tab = TabLabel(index);
        if (index == Sections().Length - 1)
        {
            ThemedMessageDialog.Show(this, "Restore defaults", "The About tab has no settings to restore.");
            return;
        }

        if (!ThemedMessageDialog.Confirm(
                this,
                "Restore defaults",
                $"Reset every setting on the {tab} tab to its default? Other tabs keep what you have.",
                confirmLabel: "Restore"))
        {
            return;
        }

        RestoreSectionDefaults(index);
    }

    /// <summary>The reset itself, without the confirm prompt.</summary>
    internal void RestoreSectionDefaults(int index)
    {
        var defaults = new Settings();
        PostCaptureMigration.Apply(defaults); // so the matrix reads as a fresh install would
        LoadSection(index, defaults);

        // The tab's toggles apply at once; its text fields still commit on Done.
        WriteToggleSettings(_settings.Current);
        _settings.Save();
        ResetValidation();
    }

    /// <summary>Runs the pending live-apply immediately instead of waiting out the
    /// coalescing delay. Test seam.</summary>
    internal void FlushLiveApplyForTest() => OnLiveApplyDue(null, EventArgs.Empty);

    internal bool HasUncommittedFieldEditsForTest() => HasUncommittedFieldEdits();

    private string TabLabel(int index) =>
        SectionList?.Items.Count > index && SectionList.Items[index] is ListBoxItem item
            ? LogicalDescendants(item).OfType<TextBlock>().LastOrDefault()?.Text ?? "this"
            : "this";

    // ------------------------------------------------------- discard guard

    /// <summary>True when a field that only commits on Done differs from what is stored.
    /// The toggles are already applied, so they are never "unsaved".</summary>
    private bool HasUncommittedFieldEdits()
    {
        var s = _settings.Current;
        if (!string.Equals(SaveFolderBox.Text.Trim(), s.SaveFolder, StringComparison.Ordinal)) return true;
        if (!string.Equals(TemplateBox.Text.Trim(), s.FileNameTemplate, StringComparison.Ordinal)) return true;
        if (FieldDiffers(RecordingFpsBox, s.RecordingFps)) return true;
        if (FieldDiffers(GifFpsBox, s.GifFps)) return true;
        if (FieldDiffers(CountdownBox, s.RecordingCountdownSeconds)) return true;
        if (FieldDiffers(WebcamSizeBox, s.WebcamOverlaySizePercent)) return true;
        if (OverlayAutoCloseCheck.IsChecked == true && FieldDiffers(OverlayCloseBox, s.OverlayAutoCloseSeconds))
            return true;

        foreach (var def in AllShortcutDefs())
        {
            if (_shortcutBoxes.TryGetValue(def.Key, out var box) &&
                !string.Equals(NormalizeForCompare(box.Text), def.Get(s), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool FieldDiffers(TextBox box, int stored) =>
        !int.TryParse(box.Text.Trim(), out int value) || value != stored;

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ConfigureAccessibility()
    {
        int tabIndex = 10;
        Accessible(StartupCheck, "Start WinShot at login", "Adds or removes WinShot from Windows startup.");
        Accessible(UpdatesCheck, "Check for updates at startup", "Checks GitHub Releases when WinShot starts.");
        Accessible(PlaySoundsCheck, "Play a sound on capture", "Play a shutter sound each time WinShot captures.");
        Accessible(ShutterSoundCombo, "Shutter sound", "Choose which shutter sound plays on capture.");
        Accessible(SaveFolderBox, "Export location", "Folder where captures are saved.");
        Accessible(BrowseSaveFolderButton, "Browse for export location", "Choose the folder where captures are saved.");
        Accessible(HideIconsCheck, "Hide desktop icons while capturing", "Temporarily hides desktop icons during capture.");
        Accessible(ScreenshotShowOverlayCheck, "Show Quick Access after a screenshot", "Show the Quick Access card after every screenshot.");
        Accessible(RecordingShowOverlayCheck, "Show Quick Access after a recording", "Show the completion card after every recording.");
        Accessible(ScreenshotCopyCheck, "Copy screenshots to the clipboard", "Put every screenshot on the clipboard.");
        Accessible(RecordingCopyCheck, "Copy recordings to the clipboard", "Put the completed recording file on the clipboard.");
        Accessible(ScreenshotSaveCheck, "Save screenshots to the export location", "Write every screenshot to the export folder.");
        Accessible(ScreenshotOpenAnnotateCheck, "Open Annotate after a screenshot", "Open the annotation editor on every screenshot.");
        Accessible(ScreenshotPinCheck, "Pin screenshots to the screen", "Pin every screenshot as a floating window.");
        Accessible(RecordingOpenEditorCheck, "Open Video Editor after a recording", "Open the local editor when a recording finishes.");

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
        Accessible(MaxResolutionCombo, "Maximum video resolution", "Choose the largest output resolution for MP4 recordings.");
        Accessible(RecordingFpsBox, "Video frames per second", "Enter a value from 10 to 60.");
        Accessible(RecordAudioCheck, "Record microphone", "Capture microphone audio in MP4 recordings.");
        Accessible(SystemAudioCheck, "Record system audio", "Capture Windows system audio in MP4 recordings.");
        Accessible(RecordAudioMonoCheck, "Record microphone in mono", "Use one microphone audio channel when microphone capture is enabled.");
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
            StartupCheck, UpdatesCheck, PlaySoundsCheck, ShutterSoundCombo, SaveFolderBox,
            BrowseSaveFolderButton, HideIconsCheck,
            ScreenshotShowOverlayCheck, RecordingShowOverlayCheck, ScreenshotCopyCheck, RecordingCopyCheck,
            ScreenshotSaveCheck, ScreenshotOpenAnnotateCheck, ScreenshotPinCheck, RecordingOpenEditorCheck,
            OverlayPositionCombo, OverlayMoveToActiveScreenCheck,
            OverlaySizeSlider, OverlayAutoCloseCheck, OverlayActionCombo, OverlayCloseBox,
            OverlayCloseAfterDragCheck, OverlaySaveBehaviorCombo, ShowRecordingControlsCheck,
            ShowRecordingTimerCheck, ScaleHiDpiVideoCheck, CaptureCursorCheck, ClickHighlightsCheck,
            KeystrokesCheck, RememberLastSelectionCheck, DimScreenCheck, CountdownBox, WebcamCombo,
            WebcamSizeBox, MaxResolutionCombo,
            RecordingFpsBox, RecordAudioCheck, SystemAudioCheck, RecordAudioMonoCheck,
            GifFpsBox, GifQualitySlider, OptimizeGifsCheck, GifSizeCombo,
            FormatCombo, HiDpiCheck, SelfTimerCombo,
            FreezeScreenCheck, CrosshairModeCombo, ShowMagnifierCheck, TemplateBox, HistorySlider,
            PinnedRoundedCornersCheck, PinnedShadowCheck, PinnedBorderCheck, AllInOneRememberCheck,
            OcrJoinLinesCheck,
        })
        {
            control.TabIndex = tabIndex++;
        }

        RestoreDefaultsButton.TabIndex = 999;
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
                MarkInvalid(box, $"{box.Text} is already assigned to another action.");
            }
            else if (ReferenceEquals(box.BorderBrush, ErrorBrush))
            {
                // Only clear marks we own (an error border with our conflict message).
                box.ClearValue(BorderBrushProperty);
                box.ToolTip = null;
                InlineValidation.Clear(box);
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
            box.ClearValue(BorderBrushProperty);
            box.ToolTip = null;
            InlineValidation.Clear(box);
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
        // The tooltip stays as the accessible/overflow copy; the adorner is what the user
        // actually reads, under the field, without hovering anything.
        box.ToolTip = message;
        InlineValidation.Show(box, message);
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
