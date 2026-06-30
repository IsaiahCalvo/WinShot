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
    public string HotkeyCaptureWindow { get; set; } = "Ctrl+Shift+8";
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

    // Extra shortcut catalog entries that mirror CleanShot's full Shortcuts list but are not yet
    // wired to behavior. Keyed by the catalog's stable action id -> gesture (e.g. "Ctrl+Alt+P").
    // Unassigned actions are simply absent. Real/global hotkeys keep their own fields above.
    public Dictionary<string, string> ShortcutBindings { get; set; } = new();

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
    public bool OcrDetectLinks { get; set; } = true;   // TODO: wire behavior — detect URLs in recognized text
    public string OcrLanguage { get; set; } = "auto";  // TODO: wire behavior — OCR recognition language ("auto" = detect)

    // History
    public int HistoryRetentionDays { get; set; } = 0; // 0 = count-based pruning only

    // Quick Access overlay (post-capture)
    public bool OverlayAutoClose { get; set; } = false; // CleanShot "Auto-close: Enable"; gates OverlayAutoCloseSeconds

    // General / app behavior (UI-only persisted toggles; behavior wired later)
    public bool PlaySounds { get; set; } = false;       // TODO: wire behavior — capture/record sound effects
    public string ShutterSound { get; set; } = "default"; // TODO: wire behavior — which shutter sound to play
    public bool ShowTrayIcon { get; set; } = true;      // TODO: wire behavior — show the menu/tray icon
    public bool CheckForUpdatesOnStartup { get; set; } = true; // poll GitHub Releases on launch

    // General > "After capture" matrix. CleanShot has a per-action checkbox for
    // Screenshot and Recording independently. (TODO: wire behavior in App.xaml.cs.)
    public bool ScreenshotShowOverlay { get; set; } = true;   // TODO: wire behavior
    public bool RecordingShowOverlay { get; set; } = true;    // TODO: wire behavior
    public bool ScreenshotCopy { get; set; } = true;          // TODO: wire behavior
    public bool RecordingCopy { get; set; } = false;          // TODO: wire behavior
    public bool ScreenshotSave { get; set; } = false;         // TODO: wire behavior
    public bool RecordingSave { get; set; } = false;          // TODO: wire behavior
    public bool ScreenshotOpenAnnotate { get; set; } = false; // TODO: wire behavior
    public bool ScreenshotOpenEditor { get; set; } = false;   // TODO: wire behavior (Open Annotate tool — screenshot only)
    public bool ScreenshotPin { get; set; } = false;          // TODO: wire behavior (Pin to the screen — screenshot only)
    public bool RecordingOpenEditor { get; set; } = false;    // TODO: wire behavior (Open Video Editor — recording only)

    // Screenshots
    public bool AddPixelBorder { get; set; } = false;   // TODO: wire behavior — add 1px border to screenshots
    public bool ConvertToSrgb { get; set; } = false;    // TODO: wire behavior — convert screenshots to sRGB color profile
    public string ScreenshotBackground { get; set; } = "none"; // TODO: wire behavior — none | wallpaper | gradient | color
    public bool CursorOnScreenshots { get; set; } = false; // TODO: wire behavior — include cursor in fullscreen/self-timer shots
    public bool FreezeScreen { get; set; } = true;      // TODO: wire behavior — freeze the screen while selecting
    public bool ShowCrosshair { get; set; } = true;     // TODO: wire behavior — crosshair guides during region select
    public string CrosshairMode { get; set; } = "command"; // always | command | never — when crosshair is shown
    public bool ShowMagnifier { get; set; } = true;     // TODO: wire behavior — pixel magnifier during region select

    // Quick Access overlay (extends the post-capture overlay block)
    public string OverlayPosition { get; set; } = "left";  // TODO: wire behavior — left | right | top | bottom etc.
    public bool OverlayMoveToActiveScreen { get; set; } = true; // TODO: wire behavior
    public int OverlaySizePercent { get; set; } = 50;      // TODO: wire behavior — overlay thumbnail size
    public string OverlayAutoCloseAction { get; set; } = "save-close"; // TODO: wire behavior — what auto-close does
    public bool OverlayCloseAfterDragging { get; set; } = true; // TODO: wire behavior
    public string OverlaySaveButtonBehavior { get; set; } = "export"; // TODO: wire behavior — export | ask

    // Recording extras (CleanShot Recording > General / Video / GIF)
    public bool ShowRecordingControls { get; set; } = true; // TODO: wire behavior — controls bar while recording
    public bool ShowRecordingTimer { get; set; } = false;   // TODO: wire behavior — display recording time in tray
    public bool ScaleHiDpiVideo { get; set; } = false;      // TODO: wire behavior — downscale HiDPI video to 1x
    public bool DoNotDisturbWhileRecording { get; set; } = false; // TODO: wire behavior
    public bool RememberLastSelection { get; set; } = false; // TODO: wire behavior — recording area
    public bool DimScreenWhileRecording { get; set; } = true; // TODO: wire behavior
    public bool ShowRecordingCountdown { get; set; } = false; // TODO: wire behavior — countdown before recording
    public string RecordingMaxResolution { get; set; } = "original"; // TODO: wire behavior — original | 4K | 1080p | 720p
    public bool RecordAudioMono { get; set; } = false;      // TODO: wire behavior — downmix mic to mono
    public bool OpenVideoEditorAfterRecording { get; set; } = false; // TODO: wire behavior
    public int GifQuality { get; set; } = 80;               // TODO: wire behavior — 0..100, GIF encode quality
    public string GifSize { get; set; } = "800";            // TODO: wire behavior — max GIF width (px) or "auto"
    public bool OptimizeGifs { get; set; } = true;          // TODO: wire behavior — palette/optimization pass

    // Annotate
    public bool InverseArrowDirection { get; set; } = false; // TODO: wire behavior
    public bool SmoothPencil { get; set; } = true;           // TODO: wire behavior
    public bool RememberLastTool { get; set; } = false;      // TODO: wire behavior — background tool remembers last
    public bool DrawShadowOnObjects { get; set; } = true;    // TODO: wire behavior
    public bool AutoExpandCanvas { get; set; } = false;      // TODO: wire behavior
    public bool ShowColorNames { get; set; } = false;        // TODO: wire behavior
    public bool AlwaysOnTop { get; set; } = false;           // TODO: wire behavior — annotate window always on top
    public bool ShowDockIcon { get; set; } = true;           // TODO: wire behavior

    // Pinned screenshots (CleanShot "Pin to screen")
    public bool PinnedRoundedCorners { get; set; } = true;  // TODO: wire behavior
    public bool PinnedShadow { get; set; } = true;          // TODO: wire behavior
    public bool PinnedBorder { get; set; } = false;         // TODO: wire behavior

    // Advanced
    public bool AskForNameAfterCapture { get; set; } = false; // TODO: wire behavior — prompt for a file name after each capture
    public bool AddRetinaSuffix { get; set; } = true;         // TODO: wire behavior — add "@2x" suffix to HiDPI screenshots
    public string CopyToClipboardFormat { get; set; } = "file-image"; // TODO: wire behavior — file-image | file | image
    public bool AllInOneRememberLast { get; set; } = true;    // TODO: wire behavior — All-in-One remembers last selection
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
