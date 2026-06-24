using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class StartupWarmupPlanTests
{
    [Fact]
    public void LightweightStartupStages_DoNotRetainHiddenUiAtStartup()
    {
        var stages = StartupWarmupPlan.LightweightStartupStages().ToList();

        Assert.All(stages, stage => Assert.Equal(StartupWarmupCost.Lightweight, stage.Cost));
        Assert.DoesNotContain(stages, stage => stage.Kind is
            StartupWarmupKind.CaptureSelectors or
            StartupWarmupKind.SettingsWindow or
            StartupWarmupKind.QuickActionsWindow or
            StartupWarmupKind.RecordingSurfaces or
            StartupWarmupKind.HistoryWindow or
            StartupWarmupKind.EditorWindow or
            StartupWarmupKind.OcrEngine or
            StartupWarmupKind.DesktopDuplication);
    }

    [Fact]
    public void OnDemandStages_KeepWarmupsOutOfTheIdleStartupPath()
    {
        var startupKinds = StartupWarmupPlan.LightweightStartupStages()
            .Select(stage => stage.Kind)
            .ToHashSet();
        var onDemandKinds = StartupWarmupPlan.OnDemandStages()
            .Select(stage => stage.Kind)
            .ToHashSet();

        Assert.Contains(StartupWarmupKind.CaptureSelectors, onDemandKinds);
        Assert.Contains(StartupWarmupKind.EditorWindow, onDemandKinds);
        Assert.Contains(StartupWarmupKind.HistoryWindow, onDemandKinds);
        Assert.Contains(StartupWarmupKind.OcrEngine, onDemandKinds);
        Assert.Empty(startupKinds.Intersect(onDemandKinds));
    }
}
