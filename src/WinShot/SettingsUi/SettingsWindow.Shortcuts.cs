using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinShot.Core;

namespace WinShot.SettingsUi;

// The Shortcuts tab is generated from a catalog that mirrors CleanShot X's full shortcut list.
// A handful of entries are "real" global hotkeys (registered with the OS, validated for
// conflicts); the rest are persisted placeholders the user can bind, wired to behavior later.
public partial class SettingsWindow
{
    /// <summary>One row in the Shortcuts catalog.</summary>
    private sealed class ShortcutDef
    {
        public string Key = "";              // stable id used for persistence + box lookup
        public string Label = "";
        public string? ValidatorLabel;       // non-null => real/global hotkey (OS-registered)
        public Func<Settings, string>? Get;  // real: read the named Settings field
        public Action<Settings, string>? Set;// real: write the named Settings field
        public bool IsReal => ValidatorLabel is not null;
    }

    private sealed class ShortcutSection
    {
        public string Title = "";
        public ShortcutDef[] Items = Array.Empty<ShortcutDef>();
    }

    private readonly Dictionary<string, HotkeyBox> _shortcutBoxes = new();
    private ShortcutSection[] _shortcutCatalog = Array.Empty<ShortcutSection>();

    private static ShortcutDef Real(
        string key, string label, string validatorLabel,
        Func<Settings, string> get, Action<Settings, string> set) =>
        new() { Key = key, Label = label, ValidatorLabel = validatorLabel, Get = get, Set = set };

    private static ShortcutDef Ph(string key, string label) =>
        new() { Key = key, Label = label };

    /// <summary>
    /// The full CleanShot-parity shortcut list. Cloud-only actions ("…&amp; Upload to Cloud")
    /// are intentionally omitted to match WinShot's no-cloud scope.
    /// </summary>
    private static ShortcutSection[] BuildShortcutCatalog() => new[]
    {
        new ShortcutSection { Title = "General", Items = new[]
        {
            Real("all-in-one", "All-In-One", "All-in-one capture",
                 s => s.HotkeyAllInOne, (s, v) => s.HotkeyAllInOne = v),
            Ph("toggle-desktop-icons", "Toggle Desktop Icons"),
            Ph("open-capture-history", "Open Capture History"),
            Ph("restore-last-capture", "Restore Last Capture"),
        }},
        new ShortcutSection { Title = "Screenshots", Items = new[]
        {
            Real("capture-area", "Capture Area", "Capture area",
                 s => s.HotkeyCaptureRegion, (s, v) => s.HotkeyCaptureRegion = v),
            Real("capture-previous", "Capture Previous Area", "Repeat previous region",
                 s => s.HotkeyCapturePrevious, (s, v) => s.HotkeyCapturePrevious = v),
            Real("capture-fullscreen", "Capture Fullscreen", "Capture fullscreen",
                 s => s.HotkeyCaptureFullscreen, (s, v) => s.HotkeyCaptureFullscreen = v),
            Real("capture-window", "Capture Window", "Capture window",
                 s => s.HotkeyCaptureWindow, (s, v) => s.HotkeyCaptureWindow = v),
            Ph("self-timer", "Self-Timer"),
            Ph("capture-copy", "Capture Area & Copy to Clipboard"),
            Ph("capture-save", "Capture Area & Save"),
            Ph("capture-annotate", "Capture Area & Annotate"),
            Ph("capture-pin", "Capture Area & Pin to the Screen"),
        }},
        new ShortcutSection { Title = "Screen Recording", Items = new[]
        {
            Real("record-screen", "Record Screen", "Record screen",
                 s => s.HotkeyRecord, (s, v) => s.HotkeyRecord = v),
            Ph("select-window", "Select Window"),
            Ph("start-video", "Start Video Recording"),
            Ph("start-gif", "Start GIF Recording"),
            Ph("pause-resume", "Pause/Resume Recording"),
            Ph("restart-recording", "Restart Recording"),
            Ph("toggle-camera-fullscreen", "Toggle Camera Fullscreen"),
        }},
        new ShortcutSection { Title = "Scrolling Capture", Items = new[]
        {
            Real("scrolling-capture", "Scrolling Capture", "Scrolling capture",
                 s => s.HotkeyScrolling, (s, v) => s.HotkeyScrolling = v),
            Ph("scrolling-start-stop", "Start/Stop Capturing"),
        }},
        new ShortcutSection { Title = "OCR", Items = new[]
        {
            Real("capture-text", "Capture Text", "Capture text (OCR)",
                 s => s.HotkeyOcr, (s, v) => s.HotkeyOcr = v),
            Ph("capture-text-linebreaks", "Capture Text With Line Breaks"),
            Ph("capture-text-no-linebreaks", "Capture Text Without Line Breaks"),
        }},
        new ShortcutSection { Title = "Quick Access Overlay", Items = new[]
        {
            Ph("overlay-toggle", "Hide/Show Overlays"),
            Ph("overlay-save-all", "Save All Overlays"),
            Ph("overlay-close-all", "Close All Overlays"),
        }},
        new ShortcutSection { Title = "Pin", Items = new[]
        {
            Ph("pin-choose", "Choose and Pin an Image"),
            Ph("pin-toggle", "Toggle Pins Visibility"),
            Ph("pin-close-all", "Close All Pins"),
            Ph("pin-last", "Pin Last Screenshot"),
        }},
        new ShortcutSection { Title = "Annotate", Items = new[]
        {
            Ph("annotate-open-file", "Open File"),
            Ph("annotate-open-clipboard", "Open From Clipboard"),
            Ph("annotate-last", "Annotate Last Screenshot"),
            Ph("annotate-copy-object", "Copy Object to Clipboard"),
            Ph("annotate-duplicate", "Duplicate Object"),
            Ph("annotate-save", "Save"),
            Ph("annotate-save-as", "Save as"),
            Ph("annotate-copy-screenshot", "Copy Screenshot to Clipboard"),
            Ph("annotate-print", "Print"),
            Ph("annotate-pin", "Pin to the Screen"),
            Ph("annotate-add-new", "Add New Screenshot"),
            Ph("annotate-add-file", "Add Screenshot From File"),
        }},
        new ShortcutSection { Title = "Annotate tools", Items = new[]
        {
            Ph("tool-increase", "Increase Tool Size"),
            Ph("tool-decrease", "Decrease Tool Size"),
            Ph("tool-background", "Background Tool"),
            Ph("tool-move", "Move Tool"),
            Ph("tool-crop", "Crop & Resize Tool"),
            Ph("tool-draw", "Draw Tool"),
            Ph("tool-highlighter", "Highlighter Tool"),
            Ph("tool-line", "Line Tool"),
            Ph("tool-text", "Text Tool"),
            Ph("tool-arrow", "Arrow Tool"),
            Ph("tool-counter", "Counter Tool"),
            Ph("tool-ellipse", "Ellipse Tool"),
            Ph("tool-redaction", "Redaction Tool"),
            Ph("tool-spotlight", "Spotlight Tool"),
            Ph("tool-rectangle", "Rectangle Tool"),
            Ph("tool-filled-rectangle", "Filled Rectangle Tool"),
        }},
    };

    private IEnumerable<ShortcutDef> AllShortcutDefs() =>
        _shortcutCatalog.SelectMany(section => section.Items);

    /// <summary>Generates the Shortcuts tab rows into ShortcutsHost from the catalog.</summary>
    private void BuildShortcutsTab()
    {
        _shortcutCatalog = BuildShortcutCatalog();
        var sepStyle = (Style)FindResource("GroupSep");
        var categoryStyle = (Style)FindResource("ShortcutCategory");
        var labelBrush = (Brush)FindResource("TextSecondaryBrush");

        bool firstSection = true;
        foreach (var section in _shortcutCatalog)
        {
            if (!firstSection)
                ShortcutsHost.Children.Add(new Rectangle { Style = sepStyle });
            firstSection = false;

            ShortcutsHost.Children.Add(new TextBlock { Style = categoryStyle, Text = section.Title });

            foreach (var item in section.Items)
            {
                // Label fills the left, recorder pinned right (matches CleanShot and avoids the
                // fixed-width label clipping long action names like "Capture Area & ...").
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };

                var box = new HotkeyBox { Width = 180 };
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

    /// <summary>Populates every shortcut box from settings (named field for real, dict for placeholders).</summary>
    private void LoadShortcutBoxes()
    {
        var s = _settings.Current;
        foreach (var def in AllShortcutDefs())
        {
            if (!_shortcutBoxes.TryGetValue(def.Key, out var box)) continue;
            box.Text = def.IsReal
                ? def.Get!(s)
                : s.ShortcutBindings.TryGetValue(def.Key, out var gesture) ? gesture : "";
        }
    }

    /// <summary>Writes every shortcut box back to settings.</summary>
    private void SaveShortcutBoxes(Settings s)
    {
        foreach (var def in AllShortcutDefs())
        {
            if (!_shortcutBoxes.TryGetValue(def.Key, out var box)) continue;
            string value = HotkeyValue(box);
            if (def.IsReal)
            {
                // Never let a blank/unbound box wipe a good global hotkey — that was the reset the
                // user hit. A real hotkey can only be CHANGED to another valid gesture here, never
                // cleared to "" (which would silently fall back to the compiled default on relaunch).
                if (value.Length > 0)
                    def.Set!(s, value);
            }
            else if (value.Length > 0)
            {
                s.ShortcutBindings[def.Key] = value;
            }
            else
            {
                s.ShortcutBindings.Remove(def.Key);
            }
        }
    }

    /// <summary>The real/global hotkey boxes, in catalog order (drives OS conflict validation).</summary>
    private HotkeyBox[] RealHotkeyBoxes() =>
        AllShortcutDefs().Where(d => d.IsReal).Select(d => _shortcutBoxes[d.Key]).ToArray();

    private HotkeyAssignmentValidator.Field[] CreateHotkeyFields()
    {
        var s = _settings.Current;
        return AllShortcutDefs()
            .Where(d => d.IsReal)
            .Select(d => new HotkeyAssignmentValidator.Field(d.ValidatorLabel!, _shortcutBoxes[d.Key], d.Get!(s)))
            .ToArray();
    }
}
