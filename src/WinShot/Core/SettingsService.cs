using System.IO;
using System.Text.Json;

namespace WinShot.Core;

public class Settings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "WinShot");

    public string ImageFormat { get; set; } = "png";
    public bool AutoCopyToClipboard { get; set; } = true;
    public int OverlayAutoCloseSeconds { get; set; } = 0; // 0 = stay until dismissed
    public int HistoryLimit { get; set; } = 200;

    public string HotkeyCaptureRegion { get; set; } = "Ctrl+Shift+1";
    public string HotkeyCaptureFullscreen { get; set; } = "Ctrl+Shift+2";
    public string HotkeyRecord { get; set; } = "Ctrl+Shift+3";
    public string HotkeyOcr { get; set; } = "Ctrl+Shift+4";
    public string HotkeyScrolling { get; set; } = "Ctrl+Shift+5";

    public int RecordingFps { get; set; } = 30;
    public int GifFps { get; set; } = 12;
    public bool RecordAudio { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = false;

    // Naming & output
    public string FileNameTemplate { get; set; } = "WinShot {date} at {time}";
    public int NextCounter { get; set; } = 1;          // consumed by the {n} token
    public bool DownscaleHiDpi { get; set; } = false;  // halve captures taken on >100% DPI displays

    // Capture behavior
    public string PostCaptureAction { get; set; } = "overlay"; // overlay | copy | save | edit | pin | background
    public int SelfTimerSeconds { get; set; } = 3;
    public string LastCaptureRegion { get; set; } = "";        // "x,y,w,h" physical screen px
    public bool HideDesktopIconsDuringCapture { get; set; } = false;
    public string HotkeyCapturePrevious { get; set; } = "Ctrl+Shift+6";
    public string HotkeyAllInOne { get; set; } = "Ctrl+Shift+7";

    // Recording extras
    public bool RecordSystemAudio { get; set; } = false;
    public bool ShowClickHighlights { get; set; } = false;
    public bool ShowKeystrokes { get; set; } = false;
    public int RecordingCountdownSeconds { get; set; } = 0;
    public bool CaptureCursor { get; set; } = true;
    public string WebcamOverlayPosition { get; set; } = "off"; // off | top-left | top-right | bottom-left | bottom-right | fullscreen
    public int WebcamOverlaySizePercent { get; set; } = RecordingOptions.DefaultWebcamSizePercent;

    // OCR
    public bool OcrJoinLines { get; set; } = false;

    // History
    public int HistoryRetentionDays { get; set; } = 0; // 0 = count-based pruning only
}

public class SettingsService
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinShot");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Settings Current { get; private set; } = new();

    /// <summary>Raised after Save() so the app can re-register hotkeys etc.</summary>
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings, using defaults", ex);
            Current = new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
        Changed?.Invoke();
    }

    public async Task SaveAsync()
    {
        string json = JsonSerializer.Serialize(Current, JsonOptions);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, json);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
        Changed?.Invoke();
    }
}
