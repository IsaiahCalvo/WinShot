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
        SaveFolderBox.Text = s.SaveFolder;
        FormatCombo.SelectedIndex = s.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AutoCopyCheck.IsChecked = s.AutoCopyToClipboard;
        OverlayCloseBox.Text = s.OverlayAutoCloseSeconds.ToString();
        HistoryLimitBox.Text = s.HistoryLimit.ToString();
        StartupCheck.IsChecked = s.LaunchAtStartup;
        RecordingFpsBox.Text = s.RecordingFps.ToString();
        GifFpsBox.Text = s.GifFps.ToString();
        RecordAudioCheck.IsChecked = s.RecordAudio;
        HotkeyRegionBox.Text = s.HotkeyCaptureRegion;
        HotkeyFullscreenBox.Text = s.HotkeyCaptureFullscreen;
        HotkeyRecordBox.Text = s.HotkeyRecord;
        HotkeyOcrBox.Text = s.HotkeyOcr;
        HotkeyScrollingBox.Text = s.HotkeyScrolling;
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ResetValidation();
        bool valid = true;

        if (string.IsNullOrWhiteSpace(SaveFolderBox.Text))
        {
            MarkInvalid(SaveFolderBox, "Choose a folder to save captures into.");
            valid = false;
        }

        int overlaySeconds = ReadInt(OverlayCloseBox, 0, 3600, ref valid);
        int historyLimit = ReadInt(HistoryLimitBox, 1, 5000, ref valid);
        int recordingFps = ReadInt(RecordingFpsBox, 10, 60, ref valid);
        int gifFps = ReadInt(GifFpsBox, 5, 20, ref valid);

        valid &= ValidateHotkey(HotkeyRegionBox);
        valid &= ValidateHotkey(HotkeyFullscreenBox);
        valid &= ValidateHotkey(HotkeyRecordBox);
        valid &= ValidateHotkey(HotkeyOcrBox);
        valid &= ValidateHotkey(HotkeyScrollingBox);

        if (!valid) return;

        var s = _settings.Current;
        s.SaveFolder = SaveFolderBox.Text.Trim();
        s.ImageFormat = FormatCombo.SelectedIndex == 1 ? "jpg" : "png";
        s.AutoCopyToClipboard = AutoCopyCheck.IsChecked == true;
        s.OverlayAutoCloseSeconds = overlaySeconds;
        s.HistoryLimit = historyLimit;
        s.RecordingFps = recordingFps;
        s.GifFps = gifFps;
        s.RecordAudio = RecordAudioCheck.IsChecked == true;
        s.HotkeyCaptureRegion = HotkeyRegionBox.Text.Trim();
        s.HotkeyCaptureFullscreen = HotkeyFullscreenBox.Text.Trim();
        s.HotkeyRecord = HotkeyRecordBox.Text.Trim();
        s.HotkeyOcr = HotkeyOcrBox.Text.Trim();
        s.HotkeyScrolling = HotkeyScrollingBox.Text.Trim();
        s.LaunchAtStartup = StartupCheck.IsChecked == true;

        ApplyStartupRegistration(s.LaunchAtStartup);
        _settings.Save();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ResetValidation()
    {
        foreach (var box in new[]
                 {
                     SaveFolderBox, OverlayCloseBox, HistoryLimitBox, RecordingFpsBox, GifFpsBox,
                     HotkeyRegionBox, HotkeyFullscreenBox, HotkeyRecordBox, HotkeyOcrBox, HotkeyScrollingBox,
                 })
        {
            box.BorderBrush = NormalBorderBrush;
            box.ToolTip = null;
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

    private static bool ValidateHotkey(TextBox box)
    {
        if (HotkeyManager.TryParseGesture(box.Text, out _, out _)) return true;
        MarkInvalid(box, "Use a gesture like Ctrl+Shift+1.");
        return false;
    }

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
