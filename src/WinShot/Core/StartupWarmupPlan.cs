namespace WinShot.Core;

public enum StartupWarmupCost
{
    Lightweight,
    Heavy,
}

public enum StartupWarmupKind
{
    CaptureSelectors,
    SettingsWindow,
    QuickActionsWindow,
    RecordingSurfaces,
    HistoryWindow,
    EditorWindow,
    OcrEngine,
    DesktopDuplication,
}

public sealed record StartupWarmupStage(
    StartupWarmupKind Kind,
    string Name,
    int DelayMs,
    StartupWarmupCost Cost);

public static class StartupWarmupPlan
{
    public static IReadOnlyList<StartupWarmupStage> LightweightStartupStages() =>
        Array.Empty<StartupWarmupStage>();

    public static IReadOnlyList<StartupWarmupStage> OnDemandStages() =>
    [
        new(
            StartupWarmupKind.CaptureSelectors,
            "capture selectors",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.SettingsWindow,
            "settings",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.QuickActionsWindow,
            "quick actions",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.RecordingSurfaces,
            "recording surfaces",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.HistoryWindow,
            "history",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.EditorWindow,
            "editor",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.OcrEngine,
            "ocr",
            0,
            StartupWarmupCost.Heavy),
        new(
            StartupWarmupKind.DesktopDuplication,
            "desktop duplication",
            0,
            StartupWarmupCost.Heavy),
    ];
}
