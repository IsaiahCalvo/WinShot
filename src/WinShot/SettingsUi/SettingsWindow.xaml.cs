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
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinShot";

    private static SettingsWindow? _instance;

    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xE8, 0x5C, 0x5C));
    private static readonly SolidColorBrush NormalBorderBrush = new(Color.FromRgb(0x55, 0x55, 0x55));

    static SettingsWindow()
    {
        ErrorBrush.Freeze();
        NormalBorderBrush.Freeze();
    }

    private readonly SettingsService _settings;
    private bool _renderPrewarmed;
    private bool _prewarmVisible;
    private bool _saving;

    public SettingsWindow(SettingsService settings)
    {
        // Load the shared theme before parsing XAML (which references theme brushes), rather
        // than relying on another window having loaded it first. Idempotent.
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _settings = settings;
        LoadFromSettings();
        WireInlineHotkeyConflictChecks();
        DarkTitleBar.Apply(this);
    }

    /// <summary>The section panels in sidebar order; index matches SectionList.SelectedIndex.</summary>
    private ScrollViewer[] Sections() =>
        new[]
        {
            SectionGeneral, SectionShortcuts, SectionCapture, SectionRecording,
            SectionOcr, SectionNaming, SectionHistory, SectionAdvanced,
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
        bool deferActivate = false;

        if (_instance is null)
        {
            var step = Stopwatch.StartNew();
            CreateInstance(settings);
            createMs = step.ElapsedMilliseconds;
        }

        var instance = _instance ?? throw new InvalidOperationException("Settings window was not created.");

        if (instance._prewarmVisible)
        {
            var step = Stopwatch.StartNew();
            instance.ResetValidation();
            resetMs = step.ElapsedMilliseconds;
            step.Restart();
            instance.LoadFromSettings();
            loadMs = step.ElapsedMilliseconds;
            if (!instance.IsMostlyWithinWorkArea())
            {
                step.Restart();
                instance.CenterOnWorkArea();
                centerMs = step.ElapsedMilliseconds;
            }
            step.Restart();
            instance.RestorePrewarmedWindow();
            showMs = step.ElapsedMilliseconds;
            deferActivate = true;
        }
        else if (!instance.IsVisible)
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

        if (deferActivate)
        {
            instance.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => instance.Activate()));
        }
        else
        {
            var activate = Stopwatch.StartNew();
            instance.Activate();
            activateMs = activate.ElapsedMilliseconds;
        }
        if (total.ElapsedMilliseconds > 50)
        {
            Log.Info(
                "Perf settings window breakdown: " +
                $"create={createMs} reset={resetMs} load={loadMs} center={centerMs} " +
                $"show={showMs} activate={activateMs} total={total.ElapsedMilliseconds} ms");
        }
        return instance;
    }

    public static void Prewarm(SettingsService settings)
    {
        if (_instance is null)
            CreateInstance(settings);
        _instance?.PrewarmRender();
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

    private void PrewarmRender()
    {
        if (_renderPrewarmed || IsVisible) return;
        _renderPrewarmed = true;

        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;
        CenterOnWorkArea();

        Show();
        FlushPrewarmRender();
        ApplyParkedWindowStyle(parked: true);
        _prewarmVisible = true;
    }

    private void RestorePrewarmedWindow()
    {
        if (!IsMostlyWithinWorkArea())
            CenterOnWorkArea();
        ShowInTaskbar = true;
        Opacity = 1;
        ApplyParkedWindowStyle(parked: false);
        _prewarmVisible = false;
    }

    private void FlushPrewarmRender()
    {
        var frame = new DispatcherFrame();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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

    private bool IsMostlyWithinWorkArea()
    {
        var area = SystemParameters.WorkArea;
        return Left < area.Right - 120 &&
               Left + Width > area.Left + 120 &&
               Top < area.Bottom - 80 &&
               Top + Height > area.Top + 80;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _prewarmVisible = false;
        ApplyParkedWindowStyle(parked: false);
        base.OnClosing(e);
    }

    private void ParkPrewarmedWindow()
    {
        if (_prewarmVisible)
            return;

        Opacity = 0;
        ApplyParkedWindowStyle(parked: true);
        _prewarmVisible = true;
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
        SelectByTag(FormatCombo, s.ImageFormat, fallbackIndex: 0);
        AutoCopyCheck.IsChecked = s.AutoCopyToClipboard;
        SelectByTag(PostActionCombo, s.PostCaptureAction, fallbackIndex: 0);
        OverlayCloseBox.Text = s.OverlayAutoCloseSeconds.ToString();
        StartupCheck.IsChecked = s.LaunchAtStartup;
        HideIconsCheck.IsChecked = s.HideDesktopIconsDuringCapture;
        HiDpiCheck.IsChecked = s.DownscaleHiDpi;

        // Hotkeys
        HotkeyRegionBox.Text = s.HotkeyCaptureRegion;
        HotkeyFullscreenBox.Text = s.HotkeyCaptureFullscreen;
        HotkeyRecordBox.Text = s.HotkeyRecord;
        HotkeyOcrBox.Text = s.HotkeyOcr;
        HotkeyScrollingBox.Text = s.HotkeyScrolling;
        HotkeyPreviousBox.Text = s.HotkeyCapturePrevious;
        HotkeyAllInOneBox.Text = s.HotkeyAllInOne;

        // Recording
        RecordingFpsBox.Text = s.RecordingFps.ToString();
        GifFpsBox.Text = s.GifFps.ToString();
        RecordAudioCheck.IsChecked = s.RecordAudio;
        SystemAudioCheck.IsChecked = s.RecordSystemAudio;
        SelectByTag(WebcamCombo, s.WebcamOverlayPosition, fallbackIndex: 0);
        WebcamSizeBox.Text = RecordingOptions.ClampWebcamSizePercent(s.WebcamOverlaySizePercent).ToString();
        ClickHighlightsCheck.IsChecked = s.ShowClickHighlights;
        KeystrokesCheck.IsChecked = s.ShowKeystrokes;
        CountdownBox.Text = s.RecordingCountdownSeconds.ToString();
        CaptureCursorCheck.IsChecked = s.CaptureCursor;

        // OCR & Scrolling
        OcrJoinLinesCheck.IsChecked = s.OcrJoinLines;

        // Naming
        TemplateBox.Text = s.FileNameTemplate;

        // History
        HistoryLimitBox.Text = s.HistoryLimit.ToString();
        RetentionDaysBox.Text = s.HistoryRetentionDays.ToString();

        // Capture & after-capture
        SelfTimerBox.Text = s.SelfTimerSeconds.ToString();

        UpdateTemplatePreview();
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
            s.AutoCopyToClipboard = AutoCopyCheck.IsChecked == true;
            s.PostCaptureAction = SelectedTag(PostActionCombo, "overlay");
            s.OverlayAutoCloseSeconds = overlaySeconds;
            s.LaunchAtStartup = StartupCheck.IsChecked == true;
            s.HideDesktopIconsDuringCapture = HideIconsCheck.IsChecked == true;
            s.DownscaleHiDpi = HiDpiCheck.IsChecked == true;

            // Hotkeys
            s.HotkeyCaptureRegion = HotkeyValue(HotkeyRegionBox);
            s.HotkeyCaptureFullscreen = HotkeyValue(HotkeyFullscreenBox);
            s.HotkeyRecord = HotkeyValue(HotkeyRecordBox);
            s.HotkeyOcr = HotkeyValue(HotkeyOcrBox);
            s.HotkeyScrolling = HotkeyValue(HotkeyScrollingBox);
            s.HotkeyCapturePrevious = HotkeyValue(HotkeyPreviousBox);
            s.HotkeyAllInOne = HotkeyValue(HotkeyAllInOneBox);

            // Recording
            s.RecordingFps = recordingFps;
            s.GifFps = gifFps;
            s.RecordAudio = RecordAudioCheck.IsChecked == true;
            s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
            s.WebcamOverlayPosition = RecordingOptions.NormalizeWebcamPosition(SelectedTag(WebcamCombo, "off"));
            s.WebcamOverlaySizePercent = RecordingOptions.ClampWebcamSizePercent(webcamSizePercent);
            s.ShowClickHighlights = ClickHighlightsCheck.IsChecked == true;
            s.ShowKeystrokes = KeystrokesCheck.IsChecked == true;
            s.RecordingCountdownSeconds = countdown;
            s.CaptureCursor = CaptureCursorCheck.IsChecked == true;

            // OCR & Scrolling
            s.OcrJoinLines = OcrJoinLinesCheck.IsChecked == true;

            // Naming
            s.FileNameTemplate = TemplateBox.Text.Trim();

            // History
            s.HistoryLimit = historyLimit;
            s.HistoryRetentionDays = retentionDays;

            // Capture & after-capture
            s.SelfTimerSeconds = selfTimer;

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

    private HotkeyBox[] AllHotkeyBoxes() =>
        new[]
        {
            HotkeyRegionBox, HotkeyFullscreenBox, HotkeyRecordBox, HotkeyOcrBox,
            HotkeyScrollingBox, HotkeyPreviousBox, HotkeyAllInOneBox,
        };

    /// <summary>
    /// Lightweight in-app feedback: after a hotkey field loses focus, flag any field that
    /// now duplicates another field's gesture. Purely cosmetic — the authoritative
    /// validation (including app-ownership probing) still runs in OnSave and is untouched.
    /// </summary>
    private void WireInlineHotkeyConflictChecks()
    {
        foreach (var box in AllHotkeyBoxes())
            box.LostKeyboardFocus += (_, _) => RefreshInlineHotkeyConflicts();
    }

    private void RefreshInlineHotkeyConflicts()
    {
        var boxes = AllHotkeyBoxes();

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
        new[]
        {
            SaveFolderBox, OverlayCloseBox, HistoryLimitBox, RetentionDaysBox, SelfTimerBox,
            RecordingFpsBox, GifFpsBox, WebcamSizeBox, CountdownBox, TemplateBox,
            HotkeyRegionBox, HotkeyFullscreenBox, HotkeyRecordBox, HotkeyOcrBox,
            HotkeyScrollingBox, HotkeyPreviousBox, HotkeyAllInOneBox,
        };

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
            box.Focus();
            return;
        }
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

    private HotkeyAssignmentValidator.Field[] CreateHotkeyFields()
    {
        var s = _settings.Current;
        return
        [
            new("Capture region / window", HotkeyRegionBox, s.HotkeyCaptureRegion),
            new("Capture fullscreen", HotkeyFullscreenBox, s.HotkeyCaptureFullscreen),
            new("Record screen", HotkeyRecordBox, s.HotkeyRecord),
            new("Capture text (OCR)", HotkeyOcrBox, s.HotkeyOcr),
            new("Scrolling capture", HotkeyScrollingBox, s.HotkeyScrolling),
            new("Repeat previous region", HotkeyPreviousBox, s.HotkeyCapturePrevious),
            new("All-in-one capture", HotkeyAllInOneBox, s.HotkeyAllInOne),
        ];
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
