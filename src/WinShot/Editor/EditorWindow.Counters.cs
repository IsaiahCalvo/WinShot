using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinShot.Editor;

/// <summary>
/// Survey's counter SERIES dropdown (AppShell.jsx:2174-2378) on WinShot's own dropdown.
///
/// A count is a named, independently numbered run of pins. "+ New Count" starts one in a fresh
/// colour; picking an existing row continues it and adopts its colour; right-clicking a row
/// offers Continue and Delete. The numbering rules themselves are in <see cref="CounterSeries"/>.
///
/// Survey's rows and behaviour, Survey's trigger content (a 10px colour dot beside the active
/// count's label), inside WinShot's SurveyDropdown — which is the same control the arrowhead
/// and line-style pickers on that row already are, so the counter row does not read as a
/// different kind of object from its neighbours.
/// </summary>
public partial class EditorWindow
{
    /// <summary>The ComboBox tag that means "+ New Count" rather than a series id.</summary>
    private const string NewCounterSeriesTag = "__new__";

    /// <summary>The series the next pin joins. Null until the first count is started.</summary>
    private string? _counterSeriesId;

    /// <summary>The active series' first number — Survey's `seriesStart`.</summary>
    private int _counterSeriesStart = CounterSeries.DefaultStart;

    /// <summary>Re-entrancy guard: rebuilding the rows moves SelectedItem, which is not a pick.</summary>
    private bool _syncingCounterSeries;

    /// <summary>Every counter pin's data, in canvas order.</summary>
    private IEnumerable<AnnotationData> CounterPins() =>
        AnnotationCanvas.Children.OfType<Grid>()
            .Select(g => g.Tag as AnnotationData)
            .Where(m => m is { Type: AnnotationData.TypeStep })!;

    /// <summary>
    /// The series the next pin joins, starting one if there is none. Survey creates the first
    /// count implicitly on the first pin rather than making the user press "+ New Count" to
    /// begin (PDFViewer.jsx:7960 reads the ref, and a null one is filled in at commit).
    /// </summary>
    private string EnsureCounterSeries()
    {
        if (!string.IsNullOrEmpty(_counterSeriesId)) return _counterSeriesId!;
        _counterSeriesId = CounterSeries.NewSeriesId();
        return _counterSeriesId;
    }

    /// <summary>
    /// "+ New Count": a fresh id, and the next colour off Survey's largest-hue-gap picker so it
    /// is visibly distinct from every count already on the shot (counterNumbering.js:173).
    /// Survey also pushes that colour into the toolbar's own picker so the next pin previews in
    /// it (PDFViewer.jsx:4478) — this does the same through the shared colour path.
    /// </summary>
    private void NewCounterSeries()
    {
        var existing = CounterSeries.List(CounterPins()).Select(s => s.Color).ToList();
        _counterSeriesId = CounterSeries.NewSeriesId();
        _counterSeriesStart = CounterSeries.DefaultStart;
        _nextStep = CounterSeries.DefaultStart;
        SetCounterSeriesPaint(CounterSeries.PickNextSeriesColor(existing));
        RefreshCounterSeriesChrome();
    }

    /// <summary>
    /// Continue an existing count: it becomes active and the toolbar adopts its colour, so the
    /// next pin matches the group it is joining (PDFViewer.jsx:4588-4596).
    /// </summary>
    private void SwitchCounterSeries(string seriesId)
    {
        var series = CounterSeries.List(CounterPins()).FirstOrDefault(s => s.SeriesId == seriesId);
        if (series.SeriesId is null) return;
        _counterSeriesId = series.SeriesId;
        _counterSeriesStart = series.Start;
        SetCounterSeriesPaint(series.Color);
        RenumberCounters();   // recomputes _nextStep for the newly active series
    }

    /// <summary>
    /// Delete a whole count — every pin carrying that id — in ONE undoable step
    /// (counterNumbering.js:72-99). The badges are kept alive by the closure and put back at
    /// the end of the canvas on undo; their z-order relative to each other is preserved, which
    /// is all the existing delete paths in this editor promise either.
    /// </summary>
    private void DeleteCounterSeries(string seriesId)
    {
        var doomed = AnnotationCanvas.Children.OfType<Grid>()
            .Where(g => g.Tag is AnnotationData { Type: AnnotationData.TypeStep } m && m.SeriesId == seriesId)
            .ToArray();
        if (doomed.Length == 0) return;

        Select(null);
        string? wasActive = _counterSeriesId;
        void Remove()
        {
            foreach (var badge in doomed) AnnotationCanvas.Children.Remove(badge);
            if (wasActive == seriesId) _counterSeriesId = null;
            RenumberCounters();
        }
        void Restore()
        {
            foreach (var badge in doomed)
                if (!AnnotationCanvas.Children.Contains(badge))
                    AnnotationCanvas.Children.Add(badge);
            _counterSeriesId = wasActive;
            RenumberCounters();
        }
        Remove();
        Push(new EditorAction(undo: Restore, redo: Remove), apply: false);
    }

    /// <summary>
    /// Sets the active count's first number. Survey derives seriesStart from the earliest pin
    /// and DISABLES the field once a series has more than one pin; WinShot keeps it editable
    /// (the superset it already shipped), so changing it restamps the whole active series and
    /// renumbers — one undoable step, like every other restyle on this row.
    /// </summary>
    private void SetCounterSeriesStart(int start)
    {
        start = Math.Max(1, start);
        int before = _counterSeriesStart;
        _counterSeriesStart = start;

        // A null active series is the LEGACY bucket, not "no series" — a project written before
        // series existed loads its pins there, and Start still has to move them.
        string activeKey = _counterSeriesId ?? CounterSeries.LegacyKey;
        var pins = CounterPins()
            .Where(m => (string.IsNullOrEmpty(m.SeriesId) ? CounterSeries.LegacyKey : m.SeriesId!) == activeKey)
            .ToArray();
        if (pins.Length == 0)
        {
            _nextStep = start;
            RefreshCounterSeriesChrome();
            return;
        }

        int[] wasStart = pins.Select(m => m.SeriesStart ?? CounterSeries.DefaultStart).ToArray();
        void Apply(int value)
        {
            _counterSeriesStart = value;
            foreach (var pin in pins) pin.SeriesStart = value;
            RenumberCounters();
        }
        Apply(start);
        Push(new EditorAction(
            undo: () =>
            {
                _counterSeriesStart = before;
                for (int i = 0; i < pins.Length; i++) pins[i].SeriesStart = wasStart[i];
                RenumberCounters();
            },
            redo: () => Apply(start)), apply: false);
    }

    /// <summary>
    /// Rebuilds the dropdown's rows from the pins actually on the canvas: "+ New Count", then a
    /// "Continue Count" heading, then one row per count (colour dot, label, pin tally) oldest
    /// first — Survey's list, in Survey's order.
    /// </summary>
    private void RefreshCounterSeriesChrome()
    {
        if (CounterSeriesBox is null) return;
        var list = CounterSeries.List(CounterPins());

        _syncingCounterSeries = true;
        try
        {
            CounterSeriesBox.Items.Clear();
            CounterSeriesBox.Items.Add(new ComboBoxItem
            {
                Content = "+ New Count",
                Tag = NewCounterSeriesTag,
            });
            if (list.Count > 0)
            {
                // A disabled row IS Survey's section label: same words, same muted weight, and
                // it cannot be picked — no second control needed to draw one line of text.
                CounterSeriesBox.Items.Add(new ComboBoxItem
                {
                    Content = "Continue Count",
                    IsEnabled = false,
                    Focusable = false,
                });
            }

            ComboBoxItem? activeRow = null;
            foreach (var series in list)
            {
                var row = new ComboBoxItem { Tag = series.SeriesId, Content = SeriesRowContent(series) };
                row.ContextMenu = SeriesRowMenu(series);
                CounterSeriesBox.Items.Add(row);
                if (series.SeriesId == _counterSeriesId) activeRow = row;
            }

            // The trigger shows the active count (dot + label), Survey's triggerContent. A
            // series with no pins yet has no row, so the trigger falls back to "+ New Count" —
            // which is exactly what the next click would do.
            CounterSeriesBox.SelectedItem = activeRow ?? CounterSeriesBox.Items[0];
        }
        finally
        {
            _syncingCounterSeries = false;
        }
    }

    /// <summary>
    /// A count's colour belongs to the SERIES, so starting or continuing one moves the
    /// toolbar's own swatch (PDFViewer.jsx:4478, :4595) — the next pin previews in the colour
    /// of the group it will join. It deliberately does NOT go through SetCurrentColor: that
    /// restyles the current selection, and picking a count is not a recolour of whatever
    /// happens to be selected.
    /// </summary>
    private void SetCounterSeriesPaint(Color color)
    {
        _color = AnnotationStyle.Opaque(color);
        SyncColorWellIndicator();
    }

    /// <summary>Survey's row: a 10px colour disc, the label, and the pin count on the right.</summary>
    private static StackPanel SeriesRowContent(CounterSeriesEntry series)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(series.Color),
            Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
        });
        panel.Children.Add(new TextBlock
        {
            Text = series.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = series.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
        });
        return panel;
    }

    /// <summary>
    /// Survey's right-click menu on a count row: Continue, and Delete
    /// (AppShell.jsx:2292-2372). A WPF ContextMenu is the platform's own version of the
    /// portalled div that does it there, so the rows get one each rather than the editor
    /// growing a second floating layer to hit-test.
    /// </summary>
    private ContextMenu SeriesRowMenu(CounterSeriesEntry series)
    {
        // DarkContextMenu / DarkMenuItem are KEYED, not implicit: an unstyled code-built menu
        // falls back to Aero2 — a white popup with black text hanging inside a dark window.
        var itemStyle = (Style)FindResource("DarkMenuItem");
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var continueItem = new MenuItem { Header = "Continue", Style = itemStyle };
        continueItem.Click += (_, _) => { menu.IsOpen = false; SwitchCounterSeries(series.SeriesId); };
        var deleteItem = new MenuItem { Header = "Delete", Style = itemStyle };
        deleteItem.Click += (_, _) => { menu.IsOpen = false; DeleteCounterSeries(series.SeriesId); };
        menu.Items.Add(continueItem);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void OnCounterSeriesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCounterSeries || _syncingContext) return;
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;
        if (tag == NewCounterSeriesTag) NewCounterSeries();
        else SwitchCounterSeries(tag);
    }
}
