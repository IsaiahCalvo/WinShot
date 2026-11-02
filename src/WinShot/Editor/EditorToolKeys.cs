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

    public static readonly IReadOnlyList<ToolKey> All = new ToolKey[]
    {
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
        new(EditorTool.Magnifier, null, Key.M, false, "Magnifier"),
        new(null, ActionBackground, Key.B, false, "Background"),
        new(null, ActionAddImage, Key.I, false, "Add image"),
    };

    /// <summary>The entry a bare letter (or Shift+letter) stands for, or null.</summary>
    public static ToolKey? Resolve(Key key, bool shift) =>
        All.FirstOrDefault(k => k.Key == key && k.Shift == shift);

    /// <summary>The plain (unshifted) badge for a tool, for its tooltip; null when it has no key.</summary>
    public static string? BadgeFor(EditorTool tool) =>
        All.FirstOrDefault(k => k.Tool == tool && !k.Shift)?.Badge
        ?? All.FirstOrDefault(k => k.Tool == tool)?.Badge;
}
