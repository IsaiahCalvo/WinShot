using System.Text.Json;
using WinShot.Core;
using WinShot.SettingsUi;
using Xunit;

namespace WinShot.Tests;

public class SettingsTruthCatalogTests
{
    [Fact]
    public void EveryPersistedSetting_IsClassified_WithoutOverlap()
    {
        string[] modelProperties = typeof(Settings).GetProperties().Select(p => p.Name).Order().ToArray();
        string[] classified = SettingsTruthCatalog.VisibleRuntimeBackedProperties
            .Concat(SettingsTruthCatalog.StoredButUnavailableProperties)
            .Concat(SettingsTruthCatalog.NonInteractiveRuntimeStateProperties)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

        Assert.Equal(modelProperties, classified);
        Assert.Empty(SettingsTruthCatalog.VisibleRuntimeBackedProperties
            .Intersect(SettingsTruthCatalog.StoredButUnavailableProperties, StringComparer.Ordinal));
        Assert.Empty(SettingsTruthCatalog.VisibleRuntimeBackedProperties
            .Intersect(SettingsTruthCatalog.NonInteractiveRuntimeStateProperties, StringComparer.Ordinal));
        Assert.Empty(SettingsTruthCatalog.StoredButUnavailableProperties
            .Intersect(SettingsTruthCatalog.NonInteractiveRuntimeStateProperties, StringComparer.Ordinal));
    }

    [Fact]
    public void ShortcutCatalog_ContainsOnlyActionsRegisteredByTheApp()
    {
        Assert.Equal(
        [
            "all-in-one", "capture-area", "capture-previous", "capture-fullscreen",
            "capture-window", "record-screen", "scrolling-capture", "capture-text",
        ],
        SettingsTruthCatalog.ExecutableShortcutIds);
    }

    [Fact]
    public void OlderUnknownShortcutBindings_RoundTripWithoutBecomingVisible()
    {
        const string json = """
            {
              "HotkeyCaptureRegion": "Ctrl+Alt+F9",
              "ShortcutBindings": {
                "annotate-print": "Ctrl+P",
                "future-action": "Ctrl+Alt+Q"
              }
            }
            """;

        var settings = JsonSerializer.Deserialize<Settings>(json)!;
        string saved = JsonSerializer.Serialize(settings);
        var reopened = JsonSerializer.Deserialize<Settings>(saved)!;

        Assert.Equal("Ctrl+P", reopened.ShortcutBindings["annotate-print"]);
        Assert.Equal("Ctrl+Alt+Q", reopened.ShortcutBindings["future-action"]);
        Assert.DoesNotContain("annotate-print", SettingsTruthCatalog.ExecutableShortcutIds);
        Assert.DoesNotContain("future-action", SettingsTruthCatalog.ExecutableShortcutIds);
    }
}
