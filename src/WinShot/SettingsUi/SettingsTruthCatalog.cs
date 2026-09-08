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
            "HotkeyCaptureRegion",
            "HotkeyCaptureWindow",
            "HotkeyCaptureFullscreen",
            "HotkeyRecord",
            "HotkeyOcr",
            "HotkeyScrolling",
            "HotkeyCapturePrevious",
            "HotkeyAllInOne",
            "HotkeyHistory",
            "HotkeySelfTimer",
            "HotkeyLoadClipboard",
            "RecordingFps",
            "GifFps",
            "RecordAudio",
            "LaunchAtStartup",
            "FileNameTemplate",
            "DownscaleHiDpi",
            "SelfTimerSeconds",
            "CursorOnScreenshots",
            "HideDesktopIconsDuringCapture",
            "RecordSystemAudio",
            "ShowClickHighlights",
            "ShowKeystrokes",
            "RecordingCountdownSeconds",
            "CaptureCursor",
            "WebcamOverlayPosition",
            "WebcamOverlaySizePercent",
            "OcrJoinLines",
            "HistoryEnabled",
            "HistoryRetentionDays",
            "CheckForUpdatesOnStartup",
            "PlaySounds",
            "ShutterSound",
            "ShowTrayIcon",
            "AskForNameAfterCapture",
            "NotifyStyleOcr",
            "NotifyStyleCopy",
            "NotifyStyleSave",
            "ScreenshotBackground",
            "ScreenshotBackgroundColor",
            "ScreenshotBackgroundShadow",
            "TrimWindowShadow",
            "FreezeScreen",
            "CrosshairMode",
            "ShowCrosshair",
            "ShowMagnifier",
            "AdjustSelectionBeforeCapturing",
            "WindowCaptureBackground",
            "WindowCapturePadding",
            "AllInOneRememberLast",
            "ScreenshotShowOverlay",
            "RecordingShowOverlay",
            "ScreenshotCopy",
            "RecordingCopy",
            "ScreenshotSave",
            "ScreenshotOpenAnnotate",
            "ScreenshotPin",
            "RecordingOpenEditor",
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
            "GifQuality",
            "GifSize",
            "OptimizeGifs",
            "PinnedRoundedCorners",
            "PinnedShadow",
            "PinnedBorder",
        };

    /// <summary>
    /// Every action Settings is allowed to offer a shortcut for, in catalog order. The eight
    /// with dedicated Hotkey* fields come first in their sections; the rest ride in
    /// <see cref="Settings.ShortcutBindings"/>. App.RegisterHotkeysCore registers exactly
    /// this list — a row here with no registration is a fake control.
    /// </summary>
    internal static readonly IReadOnlyList<string> ExecutableShortcutIds =
    [
        "all-in-one",
        "capture-area",
        "capture-previous",
        "capture-fullscreen",
        "capture-window",
        "capture-window-background",
        "capture-display",
        "self-timer",
        "record-screen",
        "record-display",
        "scrolling-capture",
        "scroll-horizontal",
        "capture-text",
        "load-clipboard",
        "open-image",
        "pin-to-screen",
        "restore-last",
        "unlock-pins",
        "toggle-desktop-icons",
        "history",
        "settings",
        "about",
    ];

    internal static readonly IReadOnlySet<string> StoredButUnavailableProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AddPixelBorder", "ConvertToSrgb",
            "DoNotDisturbWhileRecording", "ShowRecordingCountdown",
            "InverseArrowDirection", "SmoothPencil", "RememberLastTool", "DrawShadowOnObjects",
            "AutoExpandCanvas", "ShowColorNames", "AlwaysOnTop", "ShowDockIcon",
            "AddRetinaSuffix", "CopyToClipboardFormat", "OcrDetectLinks", "OcrLanguage",
        };

    internal static readonly IReadOnlySet<string> NonInteractiveRuntimeStateProperties =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "HistoryLimit",
            "NextCounter",
            "SettingsSchemaVersion",
            "LastRunVersion",
            "HasCompletedFirstRun",
            // Migration seeds: read once by PostCaptureMigration to fill the after-capture
            // matrix from the settings that preceded it, then never offered as a control.
            "PostCaptureAction",
            "AutoCopyToClipboard",
            "OpenVideoEditorAfterRecording",
            "LastCaptureRegion",
            "SelectorHintSessions",
            "LastRecordingRegion",
            "ShortcutBindings",
        };
}
