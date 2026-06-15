using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
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

    public SettingsWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadFromSettings();
    }

    /// <summary>Opens the settings window, or activates the instance that is already open.</summary>
    public static SettingsWindow Show(SettingsService settings)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow(settings);
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
        return _instance;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
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
        ClickHighlightsCheck.IsChecked = s.ShowClickHighlights;
        KeystrokesCheck.IsChecked = s.ShowKeystrokes;
        CountdownBox.Text = s.RecordingCountdownSeconds.ToString();
        CaptureCursorCheck.IsChecked = s.CaptureCursor;

        // Naming
        TemplateBox.Text = s.FileNameTemplate;

        // History
        HistoryLimitBox.Text = s.HistoryLimit.ToString();
        RetentionDaysBox.Text = s.HistoryRetentionDays.ToString();
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ResetValidation();
        bool valid = true;

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
        int selfTimer = ReadInt(SelfTimerBox, 1, 60, ref valid);
        int recordingFps = ReadInt(RecordingFpsBox, 10, 60, ref valid);
        int gifFps = ReadInt(GifFpsBox, 5, 20, ref valid);
        int countdown = ReadInt(CountdownBox, 0, 10, ref valid);

        valid &= ValidateHotkey(HotkeyRegionBox);
        valid &= ValidateHotkey(HotkeyFullscreenBox);
        valid &= ValidateHotkey(HotkeyRecordBox);
        valid &= ValidateHotkey(HotkeyOcrBox);
        valid &= ValidateHotkey(HotkeyScrollingBox);
        valid &= ValidateHotkey(HotkeyPreviousBox);
        valid &= ValidateHotkey(HotkeyAllInOneBox);

        if (!valid)
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
        s.HotkeyCaptureRegion = HotkeyRegionBox.Text.Trim();
        s.HotkeyCaptureFullscreen = HotkeyFullscreenBox.Text.Trim();
        s.HotkeyRecord = HotkeyRecordBox.Text.Trim();
        s.HotkeyOcr = HotkeyOcrBox.Text.Trim();
        s.HotkeyScrolling = HotkeyScrollingBox.Text.Trim();
        s.HotkeyCapturePrevious = HotkeyPreviousBox.Text.Trim();
        s.HotkeyAllInOne = HotkeyAllInOneBox.Text.Trim();

        // Recording
        s.RecordingFps = recordingFps;
        s.GifFps = gifFps;
        s.RecordAudio = RecordAudioCheck.IsChecked == true;
        s.RecordSystemAudio = SystemAudioCheck.IsChecked == true;
        s.WebcamOverlayPosition = SelectedTag(WebcamCombo, "off");
        s.ShowClickHighlights = ClickHighlightsCheck.IsChecked == true;
        s.ShowKeystrokes = KeystrokesCheck.IsChecked == true;
        s.RecordingCountdownSeconds = countdown;
        s.CaptureCursor = CaptureCursorCheck.IsChecked == true;

        // Naming
        s.FileNameTemplate = TemplateBox.Text.Trim();

        // History
        s.HistoryLimit = historyLimit;
        s.HistoryRetentionDays = retentionDays;
        s.SelfTimerSeconds = selfTimer;

        ApplyStartupRegistration(s.LaunchAtStartup);
        _settings.Save();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private TextBox[] AllInputBoxes() =>
        new[]
        {
            SaveFolderBox, OverlayCloseBox, HistoryLimitBox, RetentionDaysBox, SelfTimerBox,
            RecordingFpsBox, GifFpsBox, CountdownBox, TemplateBox,
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

    /// <summary>Selects the tab containing the first invalid box so the error is visible.</summary>
    private void FocusFirstInvalid()
    {
        foreach (var box in AllInputBoxes())
        {
            if (!ReferenceEquals(box.BorderBrush, ErrorBrush)) continue;
            if (FindContainingTab(box) is { } tab) tab.IsSelected = true;
            box.Focus();
            return;
        }
    }

    private static TabItem? FindContainingTab(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is TabItem tab) return tab;
            node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);
        }
        return null;
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

    private static bool ValidateHotkey(TextBox box)
    {
        if (HotkeyManager.TryParseGesture(box.Text, out _, out _)) return true;
        MarkInvalid(box, "Use a gesture like Ctrl+Shift+1.");
        return false;
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

    private static void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            string? exe = Environment.ProcessPath;
            if (enabled && exe is not null)
                key.SetValue(RunValueName, $"\"{exe}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update launch-at-startup registry value", ex);
        }
    }
}
