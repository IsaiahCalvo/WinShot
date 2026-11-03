namespace WinShot.Editor;

/// <summary>One row of the shortcut list: the key(s) and what they do.</summary>
/// <summary><paramref name="Assignable"/>: a tool row whose key the user can change.</summary>
internal sealed record ShortcutRow(string Keys, string Label, bool Assignable = false);

/// <summary>One titled group of rows.</summary>
internal sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The editor's shortcut list (Survey utils/keyboardShortcuts.js), opened with "?" or Ctrl+/.
/// The Tools section comes straight from <see cref="EditorToolKeys"/>, so the list can never
/// name a letter the handler doesn't answer.
/// </summary>
internal static class EditorShortcutSheet
{
    public static IReadOnlyList<ShortcutSection> Sections()
    {
        var tools = EditorToolKeys.All.Select(k => new ShortcutRow(k.Badge, k.Label, Assignable: true)).ToList();
        tools.Insert(1, new ShortcutRow("Alt+V", "Lasso select"));
        tools.Insert(2, new ShortcutRow("Space (hold)", "Pan"));
        return new[]
        {
            new ShortcutSection("Tools", tools),
            new ShortcutSection("View", new ShortcutRow[]
            {
                new("Ctrl+=", "Zoom in"),
                new("Ctrl+-", "Zoom out"),
                new("Ctrl+0", "Actual size (100%)"),
                new("Ctrl+1", "Fit"),
                new("Ctrl+2", "Fit width"),
                new("Ctrl+3", "Fit height"),
                new("Ctrl+Wheel", "Zoom at the pointer"),
                new("Shift+S", "Turn snapping on or off"),
            }),
            new ShortcutSection("Edit", new ShortcutRow[]
            {
                new("Ctrl+Z", "Undo"),
                new("Ctrl+Shift+Z / Ctrl+Y", "Redo"),
                new("Ctrl+C", "Copy the picked marks (the picture when nothing is picked)"),
                new("Ctrl+X", "Cut"),
                new("Ctrl+V", "Paste at the pointer"),
                new("Ctrl+D", "Duplicate"),
                new("Ctrl+Drag", "Drag a copy"),
                new("Delete / Backspace", "Delete"),
                new("Arrows", "Nudge"),
                new("Shift+Arrows", "Nudge 10×"),
                new("Ctrl+]", "Bring forward"),
                new("Ctrl+Shift+]", "Bring to front"),
                new("Ctrl+[", "Send backward"),
                new("Ctrl+Shift+[", "Send to back"),
                new("Esc", "Deselect, or cancel what you're drawing"),
                new("Ctrl+Shift+S", "Save as"),
            }),
            new ShortcutSection("Selecting", new ShortcutRow[]
            {
                new("Shift+Click", "Add to or remove from the selection"),
                new("Alt+Drag", "Move without snapping"),
                new("Right-click → Lock", "Lock a mark so it can't be moved, resized, restyled or deleted"),
                new("Text select: Drag", "Select the words in the picture"),
                new("Text select: Double / triple-click", "Select a word / a line"),
                new("Text select: Ctrl+A / Ctrl+C", "Select all the text / copy it"),
            }),
            new ShortcutSection("Drawing", new ShortcutRow[]
            {
                new("Alt+Right-drag / Ctrl+Alt+Drag", "Width (sideways) or opacity (up / down)"),
                new("Shift+Drag", "Square a box, or snap a line to 45°"),
            }),
            new ShortcutSection("Polygon & Polyline", new ShortcutRow[]
            {
                new("Click", "Place the next point"),
                new("Enter, or click the first point", "Finish the shape"),
                new("Esc", "Discard the unfinished shape"),
            }),
            new ShortcutSection("Help", new ShortcutRow[]
            {
                new("? / Ctrl+/", "Show or hide this list"),
            }),
        };
    }
}
