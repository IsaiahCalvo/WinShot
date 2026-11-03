using System.Windows.Input;

namespace WinShot.Editor;

/// <summary>One single-letter shortcut: the tool it arms (or the one-shot action it runs).</summary>
internal sealed record ToolKey(EditorTool? Tool, string? Action, Key Key, bool Shift, string Label)
{
    /// <summary>Exactly what a tooltip prints after the name and the shortcut list shows.</summary>
    public string Badge => Shift ? $"Shift+{KeyName(Key)}" : KeyName(Key);

    private static string KeyName(Key key) => key.ToString();
}

/// <summary>
/// THE one place that says which letter arms which tool (Survey utils/toolShortcuts.js): the key
/// handler, the tooltips and the shortcut list all read this table, so a tooltip can never name a
/// key the handler doesn't answer.
///
/// Survey's letters, kept wherever WinShot can: V select, P pen, H highlighter, E eraser,
/// Shift+E partial erase, R rectangle, O ellipse, G polygon, K polyline, L line, A arrow,
/// C counter, T text, Q callout. WinShot's own tools take letters Survey leaves free:
/// X redact, Shift+C crop (it used to be K, which is Survey's polyline), M magnifier, and the
/// one-shot B background and I add-image actions. Survey's Pan key is M; here M stays the
/// magnifier and Pan is Space (hold), which is what most people use anyway.
/// </summary>
internal static class EditorToolKeys
{
    public const string ActionBackground = "background";
    public const string ActionAddImage = "addImage";

    /// <summary>The keys as shipped, before any the user has changed.</summary>
    public static readonly IReadOnlyList<ToolKey> Defaults = new ToolKey[]
    {
        new(EditorTool.Pan, null, Key.M, false, "Pan"),          // Survey TOOL_SHORTCUTS: Pan is M
        new(EditorTool.Select, null, Key.V, false, "Select"),
        new(EditorTool.Freehand, null, Key.P, false, "Pen"),
        new(EditorTool.Highlighter, null, Key.H, false, "Highlighter"),
        new(EditorTool.Eraser, null, Key.E, false, "Eraser"),
        new(EditorTool.Eraser, null, Key.E, true, "Partial erase"),
        new(EditorTool.Rectangle, null, Key.R, false, "Rectangle"),
        new(EditorTool.Ellipse, null, Key.O, false, "Ellipse"),
        new(EditorTool.Polygon, null, Key.G, false, "Polygon"),
        new(EditorTool.Polyline, null, Key.K, false, "Polyline"),
        new(EditorTool.Line, null, Key.L, false, "Line"),
        new(EditorTool.Arrow, null, Key.A, false, "Arrow"),
        new(EditorTool.Step, null, Key.C, false, "Counter"),
        new(EditorTool.Text, null, Key.T, false, "Text"),
        new(EditorTool.Callout, null, Key.Q, false, "Callout"),
        new(EditorTool.Redact, null, Key.X, false, "Redact"),
        new(EditorTool.Crop, null, Key.C, true, "Crop"),
        new(EditorTool.Magnifier, null, Key.M, true, "Magnifier"), // WinShot-only; M belongs to Pan
        new(null, ActionBackground, Key.B, false, "Background"),
        new(null, ActionAddImage, Key.I, false, "Add image"),
    };

    /// <summary>
    /// The user's own keys (Survey customKeyboardShortcuts.js), by row label, as "K" or
    /// "Shift+K". Set from settings when the editor opens; unknown or broken entries are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Overrides { get; set; }

    /// <summary>Every row with the user's key in place of the shipped one where there is one.</summary>
    public static IReadOnlyList<ToolKey> All => Effective(Overrides);

    public static IReadOnlyList<ToolKey> Effective(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return Defaults;
        return Defaults.Select(k =>
            overrides.TryGetValue(k.Label, out string? text) && ParseChord(text) is (Key key, bool shift)
                ? k with { Key = key, Shift = shift }
                : k).ToArray();
    }

    /// <summary>Whether a key can be given to a tool: a letter, alone or with Shift. Shift+S
    /// (snapping) is kept, as Survey keeps its own fixed keys.</summary>
    public static bool CanUse(Key key, bool shift) =>
        key is >= Key.A and <= Key.Z && !(shift && key == Key.S);

    public static string ChordText(Key key, bool shift) => shift ? $"Shift+{key}" : key.ToString();

    public static (Key Key, bool Shift)? ParseChord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool shift = text.StartsWith("Shift+", StringComparison.OrdinalIgnoreCase);
        string name = shift ? text[6..] : text;
        if (!Enum.TryParse(name, ignoreCase: true, out Key key) || !CanUse(key, shift)) return null;
        return (key, shift);
    }

    /// <summary>
    /// Gives the row <paramref name="label"/> a new key in <paramref name="overrides"/>, or says
    /// why not (Survey assignShortcut): only letters, and never a key another row already has.
    /// Picking the shipped key again drops the override.
    /// </summary>
    public static string? Assign(IDictionary<string, string> overrides, string label, Key key, bool shift)
    {
        ToolKey? row = Defaults.FirstOrDefault(k => k.Label == label);
        if (row is null) return "This shortcut can't be changed.";
        if (!CanUse(key, shift)) return "Press a letter, alone or with Shift.";
        var now = Effective(new Dictionary<string, string>(overrides));
        ToolKey? clash = now.FirstOrDefault(k => k.Label != label && k.Key == key && k.Shift == shift);
        if (clash is not null) return $"{ChordText(key, shift)} is already used by {clash.Label}.";
        if (row.Key == key && row.Shift == shift) overrides.Remove(label);
        else overrides[label] = ChordText(key, shift);
        return null;
    }

    /// <summary>Puts a row back on its shipped key, unless another row has taken that key.</summary>
    public static string? Reset(IDictionary<string, string> overrides, string label)
    {
        ToolKey? row = Defaults.FirstOrDefault(k => k.Label == label);
        if (row is null || !overrides.ContainsKey(label)) return null;
        var now = Effective(new Dictionary<string, string>(overrides));
        ToolKey? clash = now.FirstOrDefault(k => k.Label != label && k.Key == row.Key && k.Shift == row.Shift);
        if (clash is not null) return $"{row.Badge} is now used by {clash.Label}. Change that one first.";
        overrides.Remove(label);
        return null;
    }

    /// <summary>The entry a bare letter (or Shift+letter) stands for, or null.</summary>
    public static ToolKey? Resolve(Key key, bool shift) =>
        All.FirstOrDefault(k => k.Key == key && k.Shift == shift);

    /// <summary>The plain (unshifted) badge for a tool, for its tooltip; null when it has no key.</summary>
    public static string? BadgeFor(EditorTool tool) =>
        All.FirstOrDefault(k => k.Tool == tool && !k.Shift)?.Badge
        ?? All.FirstOrDefault(k => k.Tool == tool)?.Badge;
}
