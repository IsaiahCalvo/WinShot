using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinShot.Core;

namespace WinShot.SettingsUi;

// Only executable global hotkeys are shown. Eight have dedicated Settings fields; the rest
// ride in Settings.ShortcutBindings, keyed by the same ids App.BindableCommands dispatches.
// Bindings for actions this build does not know about survive saves untouched, and are never
// presented as working shortcuts.
public partial class SettingsWindow
{
    private sealed class ShortcutDef
    {
        public string Key = "";
        public string Label = "";
        public string ValidatorLabel = "";
        public Func<Settings, string> Get = null!;
        public Action<Settings, string> Set = null!;
    }

    private sealed class ShortcutSection
    {
        public string Title = "";
        public ShortcutDef[] Items = Array.Empty<ShortcutDef>();
    }

    private readonly Dictionary<string, HotkeyBox> _shortcutBoxes = new();
    private ShortcutSection[] _shortcutCatalog = Array.Empty<ShortcutSection>();
    private bool _shortcutBoxesLoaded;

    private static ShortcutDef Real(
        string key, string label, string validatorLabel,
        Func<Settings, string> get, Action<Settings, string> set) =>
        new() { Key = key, Label = label, ValidatorLabel = validatorLabel, Get = get, Set = set };

    /// <summary>
    /// A command with no dedicated Settings field, bound through the existing
    /// <see cref="Settings.ShortcutBindings"/> dictionary. Unbound actions are simply
    /// absent from it, which is how the eight original hotkeys keep their own fields
    /// without a schema change per command added here.
    /// </summary>
    private static ShortcutDef Bound(string key, string label, string validatorLabel) =>
        new()
        {
            Key = key,
            Label = label,
            ValidatorLabel = validatorLabel,
            Get = s => s.ShortcutBindings.TryGetValue(key, out string? gesture) ? gesture : "",
            Set = (s, v) =>
            {
                if (string.IsNullOrWhiteSpace(v))
                    s.ShortcutBindings.Remove(key);
                else
                    s.ShortcutBindings[key] = v;
            },
        };

    /// <summary>The complete list of shortcuts registered and dispatched by App.xaml.cs.</summary>
    private static ShortcutSection[] BuildShortcutCatalog() =>
    [
        new ShortcutSection { Title = "General", Items =
        [
            Real("all-in-one", "All-in-one", "All-in-one capture",
                 s => s.HotkeyAllInOne, (s, v) => s.HotkeyAllInOne = v),
        ]},
        new ShortcutSection { Title = "Screenshots", Items =
        [
            Real("capture-area", "Capture region", "Capture region",
                 s => s.HotkeyCaptureRegion, (s, v) => s.HotkeyCaptureRegion = v),
            Real("capture-previous", "Capture previous region", "Repeat previous region",
                 s => s.HotkeyCapturePrevious, (s, v) => s.HotkeyCapturePrevious = v),
            Real("capture-fullscreen", "Capture fullscreen", "Capture fullscreen",
                 s => s.HotkeyCaptureFullscreen, (s, v) => s.HotkeyCaptureFullscreen = v),
            Real("capture-window", "Capture window", "Capture window",
                 s => s.HotkeyCaptureWindow, (s, v) => s.HotkeyCaptureWindow = v),
            Bound("capture-window-background", "Capture window with background", "Capture window with background"),
            Bound("capture-display", "Capture display", "Capture display"),
            Bound("self-timer", "Capture fullscreen (self-timer)", "Self-timer capture"),
        ]},
        new ShortcutSection { Title = "Screen recording", Items =
        [
            Real("record-screen", "Record screen", "Record screen",
                 s => s.HotkeyRecord, (s, v) => s.HotkeyRecord = v),
            Bound("record-display", "Record display", "Record display"),
        ]},
        new ShortcutSection { Title = "Scrolling capture", Items =
        [
            Real("scrolling-capture", "Scrolling capture", "Scrolling capture",
                 s => s.HotkeyScrolling, (s, v) => s.HotkeyScrolling = v),
            Bound("scroll-horizontal", "Horizontal scrolling capture", "Horizontal scrolling capture"),
        ]},
        new ShortcutSection { Title = "Text recognition", Items =
        [
            Real("capture-text", "Capture text", "Capture text (OCR)",
                 s => s.HotkeyOcr, (s, v) => s.HotkeyOcr = v),
        ]},
        new ShortcutSection { Title = "Windows and files", Items =
        [
            Bound("open-image", "Open image", "Open image"),
            Bound("pin-to-screen", "Pin an image to the screen", "Pin an image to the screen"),
            Bound("restore-last", "Restore recently closed", "Restore recently closed"),
            Bound("unlock-pins", "Unlock pinned windows", "Unlock pinned windows"),
            Bound("toggle-desktop-icons", "Hide or show desktop icons", "Hide or show desktop icons"),
        ]},
        new ShortcutSection { Title = "App", Items =
        [
            Bound("history", "History", "History"),
            Bound("settings", "Settings", "Settings"),
            Bound("about", "About WinShot", "About WinShot"),
        ]},
    ];

    private IEnumerable<ShortcutDef> AllShortcutDefs() =>
        _shortcutCatalog.SelectMany(section => section.Items);

    private void BuildShortcutsTab()
    {
        _shortcutCatalog = BuildShortcutCatalog();
        var sepStyle = (Style)FindResource("GroupSep");
        var categoryStyle = (Style)FindResource("ShortcutCategory");
        var rowLabelStyle = (Style)FindResource("RowLabel");

        int tabIndex = 100;
        bool firstSection = true;
        foreach (var section in _shortcutCatalog)
        {
            if (!firstSection)
                ShortcutsHost.Children.Add(new Rectangle { Style = sepStyle });
            firstSection = false;

            ShortcutsHost.Children.Add(new TextBlock { Style = categoryStyle, Text = section.Title });

            foreach (var item in section.Items)
            {
                // Same label + control column every other tab uses (RowLabel,
                // SettingsRowLabelWidth / SettingsRowIndent in Theme.xaml), so
                // switching tabs never re-anchors the content grid.
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = false };
                row.Children.Add(new TextBlock
                {
                    Style = rowLabelStyle,
                    Text = item.Label,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var box = new HotkeyBox { Width = 180, TabIndex = tabIndex++ };
                AutomationProperties.SetName(box, $"{item.Label} shortcut");
                AutomationProperties.SetHelpText(
                    box,
                    "Press the desired key combination. Clear the field to disable this shortcut.");
                row.Children.Add(box);
                _shortcutBoxes[item.Key] = box;

                ShortcutsHost.Children.Add(row);
            }
        }
    }

    private void LoadShortcutBoxes() => LoadShortcutBoxes(_settings.Current);

    /// <param name="s">The snapshot to read from — a pristine Settings when "Restore
    /// defaults" is resetting the Shortcuts tab, the live one otherwise.</param>
    private void LoadShortcutBoxes(Settings s)
    {
        foreach (var def in AllShortcutDefs())
            if (_shortcutBoxes.TryGetValue(def.Key, out var box))
                box.Text = def.Get(s);
        _shortcutBoxesLoaded = true;
    }

    private void SaveShortcutBoxes(Settings s)
    {
        if (!_shortcutBoxesLoaded)
        {
            Log.Info("SaveShortcutBoxes skipped: boxes not loaded from settings (guard against hotkey wipe)");
            return;
        }

        foreach (var def in AllShortcutDefs())
        {
            if (!_shortcutBoxes.TryGetValue(def.Key, out var box)) continue;
            def.Set(s, NormalizeForCompare(box.Text));
        }
        // Each Bound() def writes only its own key, so bindings for actions this build does
        // not know about survive untouched and stay available to a future version without
        // being advertised as executable today.
    }

    private HotkeyBox[] RealHotkeyBoxes() =>
        AllShortcutDefs().Select(d => _shortcutBoxes[d.Key]).ToArray();

    private HotkeyAssignmentValidator.Field[] CreateHotkeyFields()
    {
        var s = _settings.Current;
        return AllShortcutDefs()
            .Select(d => new HotkeyAssignmentValidator.Field(d.ValidatorLabel, _shortcutBoxes[d.Key], d.Get(s)))
            .ToArray();
    }
}
