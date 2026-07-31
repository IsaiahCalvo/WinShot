namespace WinShot.SettingsUi;

/// <summary>
/// Durable contract for the controls Settings is allowed to present. Every property in this
/// list is read by a runtime action outside Settings. Stored-but-unimplemented properties stay
/// in the serialized model for backward compatibility, but are not offered as working options.
/// </summary>
internal static class SettingsTruthCatalog
{
    internal static readonly IReadOnlySet<string> VisibleRuntimeBackedProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "SaveFolder",
            "ImageFormat",
            "AutoCopyToClipboard",
            "HotkeyCaptureRegion",
            "HotkeyCaptureWindow",
            "HotkeyCaptureFullscreen",
            "HotkeyRecord",
            "HotkeyOcr",
            "HotkeyScrolling",
            "HotkeyCapturePrevious",
            "HotkeyAllInOne",
            "RecordingFps",
            "GifFps",
            "RecordAudio",
            "LaunchAtStartup",
            "FileNameTemplate",
            "DownscaleHiDpi",
            "PostCaptureAction",
            "SelfTimerSeconds",
            "HideDesktopIconsDuringCapture",
            "RecordSystemAudio",
            "ShowClickHighlights",
            "ShowKeystrokes",
            "RecordingCountdownSeconds",
            "CaptureCursor",
            "WebcamOverlayPosition",
            "WebcamOverlaySizePercent",
            "OcrJoinLines",
            "HistoryRetentionDays",
            "CheckForUpdatesOnStartup",
            "FreezeScreen",
            "CrosshairMode",
            "ShowCrosshair",
            "ShowMagnifier",
            "AllInOneRememberLast",
        };

    internal static readonly IReadOnlyList<string> ExecutableShortcutIds =
    [
        "all-in-one",
        "capture-area",
        "capture-previous",
        "capture-fullscreen",
        "capture-window",
        "record-screen",
        "scrolling-capture",
        "capture-text",
    ];

    internal static readonly IReadOnlySet<string> StoredButUnavailableProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "PlaySounds", "ShutterSound", "ShowTrayIcon",
            "ScreenshotShowOverlay", "RecordingShowOverlay", "ScreenshotCopy", "RecordingCopy",
            "ScreenshotSave", "RecordingSave", "ScreenshotOpenAnnotate", "ScreenshotOpenEditor",
            "ScreenshotPin", "RecordingOpenEditor",
            "AddPixelBorder", "ConvertToSrgb", "ScreenshotBackground", "CursorOnScreenshots",
            "OverlayAutoClose", "OverlayPosition", "OverlayMoveToActiveScreen", "OverlaySizePercent",
            "OverlayAutoCloseAction", "OverlayCloseAfterDragging", "OverlaySaveButtonBehavior",
            "ShowRecordingControls", "ShowRecordingTimer", "ScaleHiDpiVideo",
            "DoNotDisturbWhileRecording", "RememberLastSelection", "DimScreenWhileRecording",
            "ShowRecordingCountdown", "RecordingMaxResolution", "RecordAudioMono",
            "OpenVideoEditorAfterRecording", "GifQuality", "GifSize", "OptimizeGifs",
            "InverseArrowDirection", "SmoothPencil", "RememberLastTool", "DrawShadowOnObjects",
            "AutoExpandCanvas", "ShowColorNames", "AlwaysOnTop", "ShowDockIcon",
            "PinnedRoundedCorners", "PinnedShadow", "PinnedBorder", "AskForNameAfterCapture",
            "AddRetinaSuffix", "CopyToClipboardFormat", "OcrDetectLinks", "OcrLanguage",
        };

    internal static readonly IReadOnlySet<string> NonInteractiveRuntimeStateProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "OverlayAutoCloseSeconds",
            "HistoryLimit",
            "NextCounter",
            "LastCaptureRegion",
            "ShortcutBindings",
        };
}
