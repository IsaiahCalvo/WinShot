using System.Globalization;

namespace WinShot.Editor.Background;

/// <summary>
/// Everything the background composer needs to reproduce a composition: which backdrop,
/// how much padding and inset, the shadow, the corners, the anchor and the ratio.
///
/// Round-trips as one <c>key=value,value,…</c> line in the same shape
/// <see cref="ToolPreferences"/> uses, so a whole named preset — and the "last used"
/// slot behind "Apply previous settings" — rides in the existing string-keyed settings
/// store with no new file format. A malformed field falls back to its default instead of
/// throwing: a corrupt setting must never stop the composer opening.
/// </summary>
public readonly record struct BackgroundComposerState(
    string Backdrop,
    int Padding,
    int Inset,
    bool AutoBalance,
    bool Shadow,
    int ShadowBlur,
    double ShadowOpacity,
    int CornerRadius,
    string Anchor,
    string Ratio)
{
    /// <summary>The sentinel backdrop meaning "no backdrop at all" — the None tile.</summary>
    public const string NoneBackdrop = "none";

    /// <summary>The sentinel backdrop meaning "checkerboard, exports transparent".</summary>
    public const string TransparentBackdrop = "transparent";

    public static BackgroundComposerState Default { get; } = new(
        Backdrop: "Azure",
        Padding: 64,
        Inset: 0,
        AutoBalance: false,
        Shadow: true,
        ShadowBlur: 32,
        ShadowOpacity: 0.55,
        CornerRadius: 12,
        Anchor: "CC",
        Ratio: "auto");

    public string Serialize() => string.Create(CultureInfo.InvariantCulture,
        $"{Escape(Backdrop)},{Padding},{Inset},{(AutoBalance ? 1 : 0)},{(Shadow ? 1 : 0)}," +
        $"{ShadowBlur},{ShadowOpacity:0.###},{CornerRadius},{Escape(Anchor)},{Escape(Ratio)}");

    public static BackgroundComposerState Parse(string? text)
    {
        var d = Default;
        if (string.IsNullOrWhiteSpace(text)) return d;

        string[] f = text.Split(',');
        if (f.Length < 10) return d;

        return new BackgroundComposerState(
            Backdrop: Unescape(f[0]) is { Length: > 0 } bg ? bg : d.Backdrop,
            Padding: Int(f[1], d.Padding, 0, 200),
            Inset: Int(f[2], d.Inset, 0, BackgroundLayout.MaxInset),
            AutoBalance: f[3] == "1",
            Shadow: f[4] == "1",
            ShadowBlur: Int(f[5], d.ShadowBlur, 4, 100),
            ShadowOpacity: Real(f[6], d.ShadowOpacity, 0.2, 0.8),
            CornerRadius: Int(f[7], d.CornerRadius, 0, 32),
            Anchor: Unescape(f[8]) is { Length: 2 } a ? a : d.Anchor,
            Ratio: Unescape(f[9]) is { Length: > 0 } r ? r : d.Ratio);
    }

    /// <summary>
    /// A named set of presets, round-tripping as "name=state;name=state". Names keep their
    /// order so the newest is last, and a duplicate name replaces rather than doubles.
    /// </summary>
    public static List<(string Name, BackgroundComposerState State)> ParseAll(string? text)
    {
        var list = new List<(string, BackgroundComposerState)>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            string name = Unescape(entry[..eq]);
            if (name.Length == 0) continue;
            list.RemoveAll(p => string.Equals(p.Item1, name, StringComparison.Ordinal));
            list.Add((name, Parse(entry[(eq + 1)..])));
        }
        return list;
    }

    public static string SerializeAll(IEnumerable<(string Name, BackgroundComposerState State)> presets) =>
        string.Join(';', presets.Select(p => $"{Escape(p.Name)}={p.State.Serialize()}"));

    // A preset name is user-typed, so the two delimiters have to survive it.
    private static string Escape(string? s) =>
        (s ?? "").Replace("%", "%25", StringComparison.Ordinal)
                 .Replace(",", "%2C", StringComparison.Ordinal)
                 .Replace(";", "%3B", StringComparison.Ordinal)
                 .Replace("=", "%3D", StringComparison.Ordinal);

    private static string Unescape(string s) =>
        s.Replace("%3D", "=", StringComparison.Ordinal)
         .Replace("%3B", ";", StringComparison.Ordinal)
         .Replace("%2C", ",", StringComparison.Ordinal)
         .Replace("%25", "%", StringComparison.Ordinal);

    private static int Int(string s, int fallback, int min, int max) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, min, max)
            : fallback;

    private static double Real(string s, double fallback, double min, double max) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? Math.Clamp(v, min, max)
            : fallback;
}
