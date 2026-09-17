using System.Windows.Media;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Survey's counter SERIES numbering (src/utils/counterNumbering.js), ported to
/// <see cref="CounterSeries"/>. Pure rules, no window — these run in a full suite.
///
/// The thing worth guarding is that numbering is PER SERIES: the whole point of the feature is
/// that deleting a pin renumbers its own count and leaves every other count alone. A single
/// running counter across the canvas — which is what WinShot did before — passes any test that
/// only ever looks at one series.
/// </summary>
public class CounterSeriesTests
{
    private static AnnotationData Pin(string? series, long createdAt, int? start = null,
        string color = "#FFEF4444") => new()
    {
        Type = AnnotationData.TypeStep,
        SeriesId = series,
        SeriesStart = start,
        CreatedAt = createdAt,
        Color = color,
    };

    private static int[] Numbers(params AnnotationData[] pins) =>
        pins.Select(p => p.Number ?? 0).ToArray();

    [Fact]
    public void EachSeriesIsNumberedIndependently()
    {
        var a1 = Pin("a", 10);
        var b1 = Pin("b", 20);
        var a2 = Pin("a", 30);
        var b2 = Pin("b", 40);
        CounterSeries.Renumber(new[] { a1, b1, a2, b2 });
        Assert.Equal(new[] { 1, 1, 2, 2 }, Numbers(a1, b1, a2, b2));
    }

    [Fact]
    public void NumbersFollowCreationOrderNotCanvasOrder()
    {
        var late = Pin("a", 300);
        var early = Pin("a", 100);
        var middle = Pin("a", 200);
        CounterSeries.Renumber(new[] { late, early, middle });
        Assert.Equal(new[] { 3, 1, 2 }, Numbers(late, early, middle));
    }

    [Fact]
    public void DeletingAPinRenumbersOnlyItsOwnSeries()
    {
        var a1 = Pin("a", 10);
        var a2 = Pin("a", 20);
        var a3 = Pin("a", 30);
        var b1 = Pin("b", 15);
        var b2 = Pin("b", 25);
        CounterSeries.Renumber(new[] { a1, a2, a3, b1, b2 });
        Assert.Equal(new[] { 1, 2, 3, 1, 2 }, Numbers(a1, a2, a3, b1, b2));

        CounterSeries.Renumber(new[] { a1, a3, b1, b2 });   // a2 deleted
        Assert.Equal(new[] { 1, 2, 1, 2 }, Numbers(a1, a3, b1, b2));
    }

    /// <summary>
    /// The start basis is the EARLIEST pin's, and it is mirrored back onto every pin of the
    /// series (counterNumbering.js:50-61), so any single survivor can reconstruct where the
    /// count began. A later pin carrying a stale start must not win.
    /// </summary>
    [Fact]
    public void TheEarliestPinOwnsTheSeriesStartAndItIsMirrored()
    {
        var first = Pin("a", 10, start: 7);
        var second = Pin("a", 20, start: 99);
        CounterSeries.Renumber(new[] { second, first });
        Assert.Equal(new[] { 8, 7 }, Numbers(second, first));
        Assert.Equal(7, second.SeriesStart);
        Assert.Equal(7, first.SeriesStart);
    }

    [Fact]
    public void AMissingStartReadsAsOne()
    {
        var pin = Pin("a", 10);
        CounterSeries.Renumber(new[] { pin });
        Assert.Equal(CounterSeries.DefaultStart, pin.Number);
    }

    /// <summary>
    /// Pins written before series existed carry no id. They are numbered as ONE group, which
    /// reproduces exactly what WinShot's old canvas-wide 1..n renumber did, so an older project
    /// reloads with the numbers it was saved with.
    /// </summary>
    [Fact]
    public void LegacyPinsAreOneGroupAndAreNotListedAsACount()
    {
        var l1 = Pin(null, 10);
        var l2 = Pin(null, 20);
        var named = Pin("a", 15);
        CounterSeries.Renumber(new[] { l1, l2, named });
        Assert.Equal(new[] { 1, 2, 1 }, Numbers(l1, l2, named));

        var list = CounterSeries.List(new[] { l1, l2, named });
        Assert.Single(list);
        Assert.Equal("a", list[0].SeriesId);
    }

    [Fact]
    public void RenumberReportsOnlyTheBadgesThatMoved()
    {
        var a1 = Pin("a", 10);
        var a2 = Pin("a", 20);
        CounterSeries.Renumber(new[] { a1, a2 });
        Assert.Empty(CounterSeries.Renumber(new[] { a1, a2 }));   // stable second pass

        var a0 = Pin("a", 5);
        var moved = CounterSeries.Renumber(new[] { a0, a1, a2 });
        Assert.Equal(3, moved.Count);   // everything shifts up one
    }

    [Fact]
    public void NonCounterAnnotationsAreIgnored()
    {
        var rect = new AnnotationData { Type = AnnotationData.TypeRectangle, CreatedAt = 5 };
        var pin = Pin("a", 10);
        CounterSeries.Renumber(new[] { rect, pin });
        Assert.Null(rect.Number);
        Assert.Equal(1, pin.Number);
    }

    // ------------------------------------------------------------------ the dropdown's rows

    /// <summary>
    /// "Count 1" is always the OLDEST series, whatever its id sorts like
    /// (counterNumbering.js:140-147).
    /// </summary>
    [Fact]
    public void SeriesAreLabelledByAgeNotById()
    {
        var older = Pin("zzz", 10);
        var newer = Pin("aaa", 50);
        var list = CounterSeries.List(new[] { newer, older });
        Assert.Equal(new[] { "Count 1", "Count 2" }, list.Select(s => s.Label));
        Assert.Equal(new[] { "zzz", "aaa" }, list.Select(s => s.SeriesId));
    }

    [Fact]
    public void ARowCarriesItsPinTallyColourAndStart()
    {
        var a1 = Pin("a", 10, start: 4, color: "#FF00FF00");
        var a2 = Pin("a", 20, start: 4, color: "#FF00FF00");
        var list = CounterSeries.List(new[] { a1, a2 });
        Assert.Equal(2, list[0].Count);
        Assert.Equal(4, list[0].Start);
        Assert.Equal(Color.FromRgb(0, 0xFF, 0), list[0].Color);
    }

    // ------------------------------------------------------------------ the series palette

    /// <summary>
    /// Survey's first count is red, hsl(0,70%,50%) — #d92626 — not the app's own default red
    /// (counterNumbering.js:181).
    /// </summary>
    [Fact]
    public void TheFirstSeriesColourIsHslRed() =>
        Assert.Equal(CounterSeries.FromHsl(0, 70, 50), CounterSeries.PickNextSeriesColor([]));

    /// <summary>
    /// The next colour sits in the MIDDLE of the largest unused hue gap, which is what keeps
    /// counts distinguishable however many there are. With red alone in use the whole ring is
    /// one 360-degree gap, so the next hue is 180 — cyan.
    /// </summary>
    [Fact]
    public void TheNextSeriesColourSplitsTheWidestHueGap()
    {
        Color red = CounterSeries.FromHsl(0, 70, 50);
        Assert.Equal(CounterSeries.FromHsl(180, 70, 50), CounterSeries.PickNextSeriesColor([red]));

        Color cyan = CounterSeries.FromHsl(180, 70, 50);
        // Two hues 180 apart leave two equal gaps; Survey takes the first it finds, at 90.
        Assert.Equal(CounterSeries.FromHsl(90, 70, 50), CounterSeries.PickNextSeriesColor([red, cyan]));
    }

    /// <summary>
    /// Every generated colour is a distinct hue — the point of the picker. Ten in a row must
    /// give ten different colours.
    /// </summary>
    [Fact]
    public void TenSeriesGetTenDistinctColours()
    {
        var used = new List<Color>();
        for (int i = 0; i < 10; i++) used.Add(CounterSeries.PickNextSeriesColor(used));
        Assert.Equal(10, used.Distinct().Count());
    }

    /// <summary>
    /// HSL, not HSV. Survey specifies the series palette in HSL; running it through an HSV
    /// converter at the same numbers gives a different, darker colour, which would put every
    /// generated count on the wrong swatch.
    /// </summary>
    [Fact]
    public void TheSeriesPaletteIsHslNotHsv()
    {
        Assert.Equal(Color.FromRgb(0xD9, 0x26, 0x26), CounterSeries.FromHsl(0, 70, 50));
        Assert.NotEqual(AnnotationStyle.FromHsv(0, 0.70, 0.50), CounterSeries.FromHsl(0, 70, 50));
    }

    [Fact]
    public void ASeriesIdIsUniquePerCall() =>
        Assert.NotEqual(CounterSeries.NewSeriesId(), CounterSeries.NewSeriesId());
}
