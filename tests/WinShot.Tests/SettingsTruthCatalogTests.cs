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
            "all-in-one", "measure", "capture-area", "capture-previous", "capture-fullscreen",
            "capture-window", "capture-window-background", "capture-display", "self-timer",
            "record-screen", "record-display", "scrolling-capture", "scroll-horizontal", "scroll-up",
            "capture-text", "pin-toggle-visibility", "pin-close-all", "pin-last",
            "load-clipboard", "open-image", "pin-to-screen", "restore-last",
            "unlock-pins", "toggle-desktop-icons", "history", "settings", "about",
        ],
        SettingsTruthCatalog.ExecutableShortcutIds);
        Assert.Equal(
            SettingsTruthCatalog.ExecutableShortcutIds.Count,
            SettingsTruthCatalog.ExecutableShortcutIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DefaultGlobalHotkeys_AreAllDistinct()
    {
        var s = new Settings();
        string[] defaults =
        [
            s.HotkeyCaptureRegion, s.HotkeyCaptureWindow, s.HotkeyCaptureFullscreen,
            s.HotkeyRecord, s.HotkeyOcr, s.HotkeyScrolling, s.HotkeyCapturePrevious,
            s.HotkeyAllInOne, s.HotkeyHistory, s.HotkeyMeasure,
        ];

        Assert.Equal("Ctrl+Shift+0", s.HotkeyHistory);
        Assert.Equal(defaults.Length, defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(defaults, gesture => Assert.True(HotkeyManager.TryParseGesture(gesture, out _, out _)));
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

    /// <summary>
    /// A shortcut row with nothing behind it is exactly the fake control this catalog exists
    /// to prevent. The eight originals have dedicated Hotkey* fields; every other id must be
    /// dispatched by App.BindableCommands, so the two lists are checked against each other.
    /// </summary>
    [Fact]
    public void EveryBindableShortcut_HasSomethingInTheAppToRunIt()
    {
        string[] fieldBacked =
        [
            "all-in-one", "measure", "capture-area", "capture-previous", "capture-fullscreen",
            "capture-window", "record-screen", "scrolling-capture", "capture-text",
            "history", "self-timer", "load-clipboard",
            "pin-toggle-visibility", "pin-close-all", "pin-last",
        ];

        string app = System.IO.File.ReadAllText(
            System.IO.Path.Combine(TestPaths.RepoRoot(), "src", "WinShot", "App.xaml.cs"));
        string[] dispatched = System.Text.RegularExpressions.Regex
            .Matches(app, @"yield return \(""(?<id>[a-z0-9-]+)""")
            .Select(m => m.Groups["id"].Value)
            .Order()
            .ToArray();

        string[] expected = SettingsTruthCatalog.ExecutableShortcutIds
            .Except(fieldBacked, StringComparer.Ordinal)
            .Order()
            .ToArray();

        Assert.Equal(expected, dispatched);

        // And every field-backed id really does have its own Hotkey* property.
        foreach (string id in fieldBacked)
            Assert.Contains(id, SettingsTruthCatalog.ExecutableShortcutIds);
    }
}
