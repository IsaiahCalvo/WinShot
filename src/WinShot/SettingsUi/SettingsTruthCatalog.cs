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
            "RecordingShowOverlay",
            "RecordingCopy",
            "OverlayAutoClose",
            "OverlayAutoCloseSeconds",
            "OverlayPosition",
            "OverlayMoveToActiveScreen",
            "OverlaySizePercent",
            "OverlayAutoCloseAction",
            "OverlayCloseAfterDragging",
            "OverlaySaveButtonBehavior",
            "ShowRecordingControls",
            "ShowRecordingTimer",
            "ScaleHiDpiVideo",
            "RememberLastSelection",
            "DimScreenWhileRecording",
            "RecordingMaxResolution",
            "RecordAudioMono",
            "OpenVideoEditorAfterRecording",
            "GifQuality",
            "GifSize",
            "OptimizeGifs",
            "PinnedRoundedCorners",
            "PinnedShadow",
            "PinnedBorder",
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
            "ScreenshotShowOverlay", "ScreenshotCopy",
            "ScreenshotSave", "RecordingSave", "ScreenshotOpenAnnotate", "ScreenshotOpenEditor",
            "ScreenshotPin", "RecordingOpenEditor",
            "AddPixelBorder", "ConvertToSrgb", "ScreenshotBackground", "CursorOnScreenshots",
            "DoNotDisturbWhileRecording", "ShowRecordingCountdown",
            "InverseArrowDirection", "SmoothPencil", "RememberLastTool", "DrawShadowOnObjects",
            "AutoExpandCanvas", "ShowColorNames", "AlwaysOnTop", "ShowDockIcon",
            "AskForNameAfterCapture",
            "AddRetinaSuffix", "CopyToClipboardFormat", "OcrDetectLinks", "OcrLanguage",
        };

    internal static readonly IReadOnlySet<string> NonInteractiveRuntimeStateProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "HistoryLimit",
            "NextCounter",
            "LastCaptureRegion",
            "LastRecordingRegion",
            "ShortcutBindings",
        };
}
