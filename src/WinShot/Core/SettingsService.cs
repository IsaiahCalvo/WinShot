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
}
