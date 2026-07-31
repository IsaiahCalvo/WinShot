using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinShot.Core;

namespace WinShot.SettingsUi;

// Only executable global hotkeys are shown. Older placeholder bindings remain in
// Settings.ShortcutBindings and survive saves, but are never presented as working shortcuts.
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

    /// <summary>The complete list of shortcuts registered and dispatched by App.xaml.cs.</summary>
    private static ShortcutSection[] BuildShortcutCatalog() =>
    [
        new ShortcutSection { Title = "General", Items =
        [
            Real("all-in-one", "All-In-One", "All-in-one capture",
                 s => s.HotkeyAllInOne, (s, v) => s.HotkeyAllInOne = v),
        ]},
        new ShortcutSection { Title = "Screenshots", Items =
        [
            Real("capture-area", "Capture Region", "Capture region",
                 s => s.HotkeyCaptureRegion, (s, v) => s.HotkeyCaptureRegion = v),
            Real("capture-previous", "Capture Previous Region", "Repeat previous region",
                 s => s.HotkeyCapturePrevious, (s, v) => s.HotkeyCapturePrevious = v),
            Real("capture-fullscreen", "Capture Fullscreen", "Capture fullscreen",
                 s => s.HotkeyCaptureFullscreen, (s, v) => s.HotkeyCaptureFullscreen = v),
            Real("capture-window", "Capture Window", "Capture window",
                 s => s.HotkeyCaptureWindow, (s, v) => s.HotkeyCaptureWindow = v),
        ]},
        new ShortcutSection { Title = "Screen Recording", Items =
        [
            Real("record-screen", "Record Screen", "Record screen",
                 s => s.HotkeyRecord, (s, v) => s.HotkeyRecord = v),
        ]},
        new ShortcutSection { Title = "Scrolling Capture", Items =
        [
            Real("scrolling-capture", "Scrolling Capture", "Scrolling capture",
                 s => s.HotkeyScrolling, (s, v) => s.HotkeyScrolling = v),
        ]},
        new ShortcutSection { Title = "OCR", Items =
        [
            Real("capture-text", "Capture Text", "Capture text (OCR)",
                 s => s.HotkeyOcr, (s, v) => s.HotkeyOcr = v),
        ]},
    ];

    private IEnumerable<ShortcutDef> AllShortcutDefs() =>
        _shortcutCatalog.SelectMany(section => section.Items);

    private void BuildShortcutsTab()
    {
        _shortcutCatalog = BuildShortcutCatalog();
        var sepStyle = (Style)FindResource("GroupSep");
        var categoryStyle = (Style)FindResource("ShortcutCategory");
        var labelBrush = (Brush)FindResource("TextSecondaryBrush");

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
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };
                var box = new HotkeyBox { Width = 180, TabIndex = tabIndex++ };
                AutomationProperties.SetName(box, $"{item.Label} shortcut");
                AutomationProperties.SetHelpText(
                    box,
                    "Press the desired key combination. Clear the field to disable this shortcut.");
                DockPanel.SetDock(box, Dock.Right);
                row.Children.Add(box);
                _shortcutBoxes[item.Key] = box;

                row.Children.Add(new TextBlock
                {
                    Text = item.Label,
                    Foreground = labelBrush,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 12, 0),
                });

                ShortcutsHost.Children.Add(row);
            }
        }
    }

    private void LoadShortcutBoxes()
    {
        var s = _settings.Current;
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
        // Intentionally do not mutate ShortcutBindings: old, unknown action bindings remain
        // available to a future version without being advertised as executable today.
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
