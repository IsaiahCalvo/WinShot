using System.Globalization;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Size presets and clamping for the annotation size field.
///
/// Replaces the old fixed 2 / 4 / 6 thickness buttons. The field accepts any whole
/// number in the tool's range; the chevron offers a preset list whose values are
/// Survey's (1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 32, 50) so the two apps feel the same.
/// Text and Step keep deriving their font size / badge diameter from this one value
/// (<see cref="AnnotationFactory.FontSizeFor"/> and the badge's 22 + 3t), so widening
/// the range needed no new semantics — only a wider label.
/// </summary>
internal static class AnnotationSize
{
    /// <summary>Survey's ANNOTATION_SIZE_PRESETS.width, verbatim.</summary>
    public static readonly int[] Presets = { 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 32, 50 };

    public const int MinWidth = 1;
    public const int MaxWidth = 50;

    /// <summary>
    /// Tools whose size reads as an overall "Size" rather than a stroke "Width" — the
    /// value drives a font size or badge diameter instead of a stroke.
    /// </summary>
    public static bool IsOverallSize(EditorTool tool) =>
        tool is EditorTool.Text or EditorTool.Step;

    public static string LabelFor(EditorTool tool) => IsOverallSize(tool) ? "Size" : "Width";

    public static int Clamp(int value) => Math.Clamp(value, MinWidth, MaxWidth);

    public static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value), MinWidth, MaxWidth) : MinWidth;

    /// <summary>
    /// Parses digits typed into the size field. Anything non-numeric leaves the value
    /// unchanged (returns false) rather than snapping to a default mid-edit.
    /// </summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        Span<char> digits = stackalloc char[8];
        int n = 0;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c)) continue;
            if (n == digits.Length) return false; // absurdly long — treat as unparseable
            digits[n++] = c;
        }
        if (n == 0) return false;

        if (!int.TryParse(digits[..n], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            return false;

        value = Clamp(parsed);
        return true;
    }

    /// <summary>
    /// Row thickness (DIP) for a preset's preview line, so the menu shows the real
    /// relative weight. Square-rooted like Survey's preview so 50 does not dwarf the rest.
    /// </summary>
    public static double PreviewThickness(int preset)
    {
        int min = Presets[0];
        int max = Presets[^1];
        if (max <= min) return 1;
        double t = Math.Sqrt(Math.Clamp((preset - min) / (double)(max - min), 0, 1));
        return 1 + Math.Round(13 * t);
    }
}

/// <summary>
/// One tool's remembered look: border colour + opacity, fill colour + opacity, size.
///
/// Fill opacity 0 means "no fill" — that is what retires the old three-way
/// None / Quarter / Solid enum from the UI. <see cref="ShapeFillMode"/> still exists
/// for reading older saved projects.
/// </summary>
internal readonly record struct ToolStyle(
    Color BorderColor,
    int BorderOpacity,
    Color FillColor,
    int FillOpacity,
    int Width)
{
    /// <summary>Border stays legible: below ~25% a stroke effectively disappears.</summary>
    public const int MinBorderOpacity = 25;

    public double BorderAlpha => Math.Clamp(BorderOpacity, MinBorderOpacity, 100) / 100.0;

    public double FillAlpha => Math.Clamp(FillOpacity, 0, 100) / 100.0;

    public bool HasFill => FillOpacity > 0;

    public ToolStyle WithBorderColor(Color c) => this with { BorderColor = c };

    public ToolStyle WithBorderOpacity(int pct) =>
        this with { BorderOpacity = Math.Clamp(pct, MinBorderOpacity, 100) };

    public ToolStyle WithFillColor(Color c) => this with { FillColor = c };

    public ToolStyle WithFillOpacity(int pct) => this with { FillOpacity = Math.Clamp(pct, 0, 100) };

    public ToolStyle WithWidth(int width) => this with { Width = AnnotationSize.Clamp(width) };

    /// <summary>Border colour with its opacity baked into the alpha channel.</summary>
    public Color BorderBrushColor => Color.FromArgb(
        (byte)Math.Round(BorderAlpha * 255), BorderColor.R, BorderColor.G, BorderColor.B);

    /// <summary>Fill colour with its opacity baked in, or null when there is no fill.</summary>
    public Color? FillBrushColor => HasFill
        ? Color.FromArgb((byte)Math.Round(FillAlpha * 255), FillColor.R, FillColor.G, FillColor.B)
        : null;
}

/// <summary>
/// Per-tool style memory. Picking up the highlighter gives back the fat translucent
/// yellow marker; switching to the pen restores a thin red line. Ported from Survey's
/// DEFAULT_TOOL_PREFERENCES / useDocumentToolPreferences, retuned to WinShot's palette.
///
/// Survey keys these per document; WinShot has no document, so a single set is kept
/// for the session and persisted with the other editor settings.
/// </summary>
internal sealed class ToolPreferences
{
    /// <summary>WinShot's annotation red (Theme.xaml AnnotationRedColor).</summary>
    public static readonly Color DefaultRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    private static readonly Color HighlighterYellow = Color.FromRgb(0xFF, 0xFF, 0x00);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

    private readonly Dictionary<EditorTool, ToolStyle> _styles = new();

    public ToolPreferences()
    {
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
            _styles[tool] = DefaultFor(tool);
    }

    /// <summary>Survey's per-tool defaults, mapped onto WinShot's tools.</summary>
    public static ToolStyle DefaultFor(EditorTool tool) => tool switch
    {
        // Freehand is Survey's pen: thin red line.
        EditorTool.Freehand => new ToolStyle(DefaultRed, 100, White, 0, 3),

        // The marker: fat, yellow, half-transparent.
        EditorTool.Highlighter => new ToolStyle(HighlighterYellow, 50, White, 0, 20),

        // Shapes carry a fill channel, off by default.
        EditorTool.Rectangle or EditorTool.Ellipse => new ToolStyle(DefaultRed, 100, White, 0, 2),

        EditorTool.Line or EditorTool.Arrow or EditorTool.CurvedArrow =>
            new ToolStyle(DefaultRed, 100, White, 0, 2),

        // Text size and badge diameter derive from Width; 4 reproduces today's defaults
        // (27pt text, 34px badge) so nothing shifts for existing muscle memory.
        EditorTool.Text or EditorTool.Step => new ToolStyle(DefaultRed, 100, White, 0, 4),

        _ => new ToolStyle(DefaultRed, 100, White, 0, 4),
    };

    /// <summary>Only rectangles and ellipses have a fill channel.</summary>
    public static bool SupportsFill(EditorTool tool) =>
        tool is EditorTool.Rectangle or EditorTool.Ellipse;

    public ToolStyle For(EditorTool tool) =>
        _styles.TryGetValue(tool, out var style) ? style : DefaultFor(tool);

    public void Set(EditorTool tool, ToolStyle style) => _styles[tool] = style;

    /// <summary>Applies a change to one tool's style and returns the updated value.</summary>
    public ToolStyle Update(EditorTool tool, Func<ToolStyle, ToolStyle> change)
    {
        var next = change(For(tool));
        _styles[tool] = next;
        return next;
    }

    public void Reset(EditorTool tool) => _styles[tool] = DefaultFor(tool);

    public void ResetAll()
    {
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
            _styles[tool] = DefaultFor(tool);
    }

    // ------------------------------------------------------------ persistence

    /// <summary>
    /// Round-trips as "tool=border,borderOpacity,fill,fillOpacity,width" lines so the
    /// set can ride in the existing string-keyed settings store. Unknown tools and
    /// malformed entries are skipped rather than throwing — a corrupt setting must
    /// never stop the editor opening.
    /// </summary>
    public string Serialize()
    {
        var parts = new List<string>();
        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            var s = For(tool);
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tool}={Hex(s.BorderColor)},{s.BorderOpacity},{Hex(s.FillColor)},{s.FillOpacity},{s.Width}"));
        }
        return string.Join(';', parts);
    }

    public static ToolPreferences Deserialize(string? text)
    {
        var prefs = new ToolPreferences();
        if (string.IsNullOrWhiteSpace(text)) return prefs;

        foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            if (!Enum.TryParse(entry[..eq], out EditorTool tool)) continue;

            string[] f = entry[(eq + 1)..].Split(',');
            if (f.Length != 5) continue;
            if (!TryHex(f[0], out Color border)) continue;
            if (!TryHex(f[2], out Color fill)) continue;
            if (!int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int bo)) continue;
            if (!int.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out int fo)) continue;
            if (!int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out int w)) continue;

            prefs.Set(tool, new ToolStyle(
                border,
                Math.Clamp(bo, ToolStyle.MinBorderOpacity, 100),
                fill,
                Math.Clamp(fo, 0, 100),
                AnnotationSize.Clamp(w)));
        }
        return prefs;
    }

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryHex(string? s, out Color color)
    {
        color = DefaultRed;
        if (s is not { Length: 6 }) return false;
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }
}
