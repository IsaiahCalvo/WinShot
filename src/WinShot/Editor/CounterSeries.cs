using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>One counter series, as the dropdown lists it.</summary>
/// <param name="SeriesId">Stable identity, stamped on every pin of the series.</param>
/// <param name="Label">"Count 1", "Count 2", … by series AGE, not by id.</param>
/// <param name="Color">The series' paint — Survey applies a colour to the whole series.</param>
/// <param name="Count">How many pins carry this id.</param>
/// <param name="FirstCreatedAt">The earliest pin's stamp; what the list is ordered by.</param>
/// <param name="Start">The series' first displayed number.</param>
internal readonly record struct CounterSeriesEntry(
    string SeriesId, string Label, Color Color, int Count, long FirstCreatedAt, int Start);

/// <summary>
/// Survey's counter SERIES model (src/utils/counterNumbering.js), ported whole.
///
/// A counter pin belongs to a series. Numbering is per series, not per document: pins are
/// grouped by <see cref="AnnotationData.SeriesId"/>, sorted by <see cref="AnnotationData.CreatedAt"/>,
/// and the i-th pin of a group shows <c>seriesStart + i</c>. Deleting a pin renumbers the rest
/// of ITS series and leaves every other series alone — which is the whole point of the feature,
/// and why numbering cannot be a single running counter.
///
/// Pure and WPF-free apart from <see cref="Color"/>, so the numbering rules are testable
/// without a window.
/// </summary>
internal static class CounterSeries
{
    /// <summary>
    /// Survey's bucket for pins written before series existed (counterNumbering.js:23). They
    /// are numbered as one group so a legacy project reads back sanely, and they are left OUT
    /// of <see cref="List"/> so they never masquerade as a named Count.
    /// </summary>
    public const string LegacyKey = "__legacy__";

    /// <summary>Survey's default when a series carries no start (counterNumbering.js:53).</summary>
    public const int DefaultStart = 1;

    /// <summary>Survey's fixed saturation/lightness for a generated series colour.</summary>
    public const double SeriesSaturation = 70;
    public const double SeriesLightness = 50;

    private static string KeyOf(AnnotationData a) =>
        string.IsNullOrEmpty(a.SeriesId) ? LegacyKey : a.SeriesId!;

    private static bool IsCounter(AnnotationData a) => a.Type == AnnotationData.TypeStep;

    /// <summary>
    /// Assigns every counter its displayed number, in place, and reports which ones changed so
    /// the caller only repaints those badges.
    ///
    /// The start basis is the EARLIEST pin's own <see cref="AnnotationData.SeriesStart"/>, and
    /// it is mirrored back onto every pin of the series (counterNumbering.js:50-61): any single
    /// surviving pin is then enough to reconstruct where the series began, which is what makes
    /// a partial delete safe.
    /// </summary>
    public static IReadOnlyList<AnnotationData> Renumber(IEnumerable<AnnotationData> annotations)
    {
        var changed = new List<AnnotationData>();
        foreach (var group in annotations.Where(IsCounter).GroupBy(KeyOf))
        {
            // OrderBy is a stable sort, so pins stamped in the same millisecond keep the order
            // they were added in rather than shuffling on every renumber.
            var list = group.OrderBy(a => a.CreatedAt ?? 0).ToList();
            int startBasis = list.Count > 0 ? list[0].SeriesStart ?? DefaultStart : DefaultStart;
            for (int i = 0; i < list.Count; i++)
            {
                int number = startBasis + i;
                if (list[i].Number == number && list[i].SeriesStart == startBasis) continue;
                list[i].Number = number;
                list[i].SeriesStart = startBasis;
                changed.Add(list[i]);
            }
        }
        return changed;
    }

    /// <summary>
    /// The dropdown's rows: one per series, oldest first, labelled "Count 1", "Count 2", … by
    /// AGE (counterNumbering.js:140-147). Legacy pins are skipped entirely — Survey does the
    /// same (:119), because an unnamed bucket cannot be continued or deleted as a series.
    /// </summary>
    public static IReadOnlyList<CounterSeriesEntry> List(IEnumerable<AnnotationData> annotations)
    {
        var rows = new List<CounterSeriesEntry>();
        foreach (var group in annotations
                     .Where(a => IsCounter(a) && !string.IsNullOrEmpty(a.SeriesId))
                     .GroupBy(a => a.SeriesId!))
        {
            long first = long.MaxValue;
            AnnotationData? earliest = null;
            foreach (var pin in group)
            {
                long ts = pin.CreatedAt ?? 0;
                if (ts < first || earliest is null) { first = ts; earliest = pin; }
            }
            rows.Add(new CounterSeriesEntry(
                group.Key,
                Label: "",
                Color: ColorOf(earliest!),
                Count: group.Count(),
                FirstCreatedAt: first == long.MaxValue ? 0 : first,
                Start: earliest!.SeriesStart ?? DefaultStart));
        }
        rows.Sort((a, b) => a.FirstCreatedAt.CompareTo(b.FirstCreatedAt));
        for (int i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { Label = $"Count {i + 1}" };
        return rows;
    }

    private static Color ColorOf(AnnotationData a) =>
        a.Color is string hex && AnnotationStyle.TryParseHex(hex, out var c) ? c : FallbackColor;

    /// <summary>Survey's fallback pin fill, '#ef4444' (counterNumbering.js:124).</summary>
    public static readonly Color FallbackColor = Color.FromRgb(0xEF, 0x44, 0x44);

    /// <summary>
    /// Survey's next-series colour (counterNumbering.js:173-198): take the hues already in use,
    /// find the LARGEST gap on the 0..360 ring, and sit in the middle of it, at a fixed S 70 /
    /// L 50. With nothing in use it starts at red. Picking the widest gap is what keeps two
    /// series visually distinguishable no matter how many there already are.
    /// </summary>
    public static Color PickNextSeriesColor(IEnumerable<Color> existing)
    {
        var hues = existing.Select(HueOf).OrderBy(h => h).ToList();
        if (hues.Count == 0) return FromHsl(0, SeriesSaturation, SeriesLightness);

        double bestGap = -1, bestMid = 0;
        for (int i = 0; i < hues.Count; i++)
        {
            double a = hues[i];
            double b = i == hues.Count - 1 ? hues[0] + 360 : hues[i + 1];
            double gap = b - a;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestMid = (a + gap / 2) % 360;
            }
        }
        return FromHsl(bestMid, SeriesSaturation, SeriesLightness);
    }

    private static double HueOf(Color c) => AnnotationStyle.ToHsv(c).h;

    /// <summary>
    /// HSL -> RGB, Survey's `hslToHex` verbatim (counterNumbering.js:243-263). Not HSV: the
    /// series palette is specified in HSL, and <see cref="AnnotationStyle.FromHsv"/> at S=0.7
    /// V=0.5 is a different, darker colour.
    /// </summary>
    public static Color FromHsl(double h, double s, double l)
    {
        double sN = s / 100, lN = l / 100;
        double c = (1 - Math.Abs(2 * lN - 1)) * sN;
        double hh = ((h % 360) + 360) % 360;
        double x = c * (1 - Math.Abs(hh / 60 % 2 - 1));
        double m = lN - c / 2;
        (double r, double g, double b) p = hh switch
        {
            < 60 => (c, x, 0),
            < 120 => (x, c, 0),
            < 180 => (0, c, x),
            < 240 => (0, x, c),
            < 300 => (x, 0, c),
            _ => (c, 0, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((p.r + m) * 255),
            (byte)Math.Round((p.g + m) * 255),
            (byte)Math.Round((p.b + m) * 255));
    }

    /// <summary>A fresh series identity. Survey's shape is `series-&lt;epoch ms&gt;`
    /// (PDFViewer.jsx:4470); the suffix keeps two counts made in the same millisecond apart.</summary>
    public static string NewSeriesId() =>
        $"series-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..40];
}
