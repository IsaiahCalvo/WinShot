namespace WinShot.Capture;

/// <summary>One aspect-ratio choice on the all-in-one size bar.</summary>
/// <param name="Label">Menu text.</param>
/// <param name="Ratio">Width ÷ height, or null for Freeform.</param>
/// <param name="Badge">Short form painted under the crop glyph; empty for Freeform.</param>
public readonly record struct AspectRatioPreset(string Label, double? Ratio, string Badge);

/// <summary>
/// The ratio list behind the size bar's crop menu. Before this the only aspect control was a
/// lock over whatever numbers happened to be typed in the width and height fields, so framing
/// a 16:9 region meant knowing and entering two numbers first.
/// </summary>
public static class AspectRatioPresets
{
    /// <summary>Freeform first, then the same eleven ratios the reference offers, in order.</summary>
    public static IReadOnlyList<AspectRatioPreset> All { get; } =
    [
        new("Freeform", null, string.Empty),
        new("1:1", 1d, "1:1"),
        new("5:4", 5d / 4d, "5:4"),
        new("4:3", 4d / 3d, "4:3"),
        new("7:5", 7d / 5d, "7:5"),
        new("3:2", 3d / 2d, "3:2"),
        new("16:10", 16d / 10d, "16:10"),
        new("16:9", 16d / 9d, "16:9"),
        new("1.85:1", 1.85d, "1.85"),
        new("2.35:1", 2.35d, "2.35"),
        new("2:3", 2d / 3d, "2:3"),
        new("9:16", 9d / 16d, "9:16"),
    ];

    /// <summary>Index 0 (Freeform) for anything out of range, so a bad index cannot lock a drag.</summary>
    public static AspectRatioPreset At(int index) =>
        index >= 0 && index < All.Count ? All[index] : All[0];

    /// <summary>The height a preset implies for a given width, for the size fields.</summary>
    public static int HeightFor(int width, double ratio) =>
        ratio > 0 ? Math.Max(1, (int)Math.Round(width / ratio)) : Math.Max(1, width);
}
