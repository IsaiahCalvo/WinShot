using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Survey's counter SERIES dropdown, end to end: pins dropped through the in-process pointer
/// seam, counts started and continued through the real control, a whole count deleted and undone.
///
/// The pure numbering rules are in CounterSeriesTests. What this adds is the wiring: that a pin
/// actually lands in the active count, that "+ New Count" is reachable and resets the run, and
/// that deleting a count is one undoable step.
///
/// LANDMINE (see .claudehelper/HANDOFF.md): this assembly gets exactly one
/// System.Windows.Application, so this class is env-gated like every other editor-window class
/// and must be run ALONE:
///     $env:WINSHOT_EDITOR_TESTS='1'
///     powershell -File .\tools\run-tests.ps1 -Filter 'FullyQualifiedName~EditorCounterSeriesTests'
/// A stood-down run reports as a PASS — never read a green filtered run as coverage unless the
/// gate was set.
/// </summary>
public class EditorCounterSeriesTests
{
    /// <summary>
    /// Three pins, then "+ New Count", then two more: the second run restarts at 1, the first
    /// keeps its numbers, and the dropdown lists both counts oldest-first with their tallies.
    /// A single running counter — which is what this editor did before — would number the last
    /// two 4 and 5, so this is the assertion the whole feature rests on.
    /// </summary>
    [Fact]
    public void ANewCountRestartsAtOneAndLeavesTheFirstCountAlone()
    {
        int[] firstRun = [], secondRun = [];
        string[] labels = [];
        int[] tallies = [];
        Color before = Colors.Transparent, after = Colors.Transparent;
        bool ran = false;

        WithEditor((editor, arm, drop, meta) =>
        {
            arm(EditorTool.Step);
            drop(120, 120); drop(200, 120); drop(280, 120);
            string? seriesOne = meta().Select(m => m.SeriesId).FirstOrDefault();
            firstRun = meta().Select(m => m.Number ?? 0).ToArray();

            before = (Color)Field("_color").GetValue(editor)!;
            Invoke(editor, "NewCounterSeries");
            after = (Color)Field("_color").GetValue(editor)!;

            drop(120, 300); drop(200, 300);
            secondRun = meta().Where(m => m.SeriesId != seriesOne).Select(m => m.Number ?? 0).ToArray();
            firstRun = meta().Where(m => m.SeriesId == seriesOne).Select(m => m.Number ?? 0).ToArray();

            var rows = CounterSeries.List(meta());
            labels = rows.Select(r => r.Label).ToArray();
            tallies = rows.Select(r => r.Count).ToArray();
            ran = true;
        });

        if (!ran) return;   // stood down: no Application to own
        Assert.Equal(new[] { 1, 2, 3 }, firstRun);
        Assert.Equal(new[] { 1, 2 }, secondRun);
        Assert.Equal(new[] { "Count 1", "Count 2" }, labels);
        Assert.Equal(new[] { 3, 2 }, tallies);
        // A new count takes a fresh colour off the hue-gap picker so it reads as a new count.
        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// The dropdown is the user's way in, so the rows must actually be there: "+ New Count",
    /// the "Continue Count" heading (disabled — it is a label, not a choice), and one row per
    /// count. The trigger shows the ACTIVE count.
    /// </summary>
    [Fact]
    public void TheDropdownListsNewCountThenAHeadingThenEveryCount()
    {
        string?[] tags = [];
        bool headingDisabled = false, triggerIsActive = false;
        bool ran = false;

        WithEditor((editor, arm, drop, meta) =>
        {
            arm(EditorTool.Step);
            drop(120, 120);
            Invoke(editor, "NewCounterSeries");
            drop(200, 300);
            Invoke(editor, "RefreshCounterSeriesChrome");

            var box = (ComboBox)editor.FindName("CounterSeriesBox")!;
            tags = box.Items.OfType<ComboBoxItem>().Select(i => i.Tag as string).ToArray();
            headingDisabled = box.Items.OfType<ComboBoxItem>().Any(i => !i.IsEnabled);
            string? active = (string?)Field("_counterSeriesId").GetValue(editor);
            triggerIsActive = (box.SelectedItem as ComboBoxItem)?.Tag as string == active;
            ran = true;
        });

        if (!ran) return;
        Assert.Equal("__new__", tags[0]);
        Assert.Null(tags[1]);                       // the "Continue Count" heading carries no tag
        Assert.True(headingDisabled);
        Assert.Equal(4, tags.Length);               // "+ New Count", heading, Count 1, Count 2
        Assert.True(triggerIsActive);
    }

    /// <summary>
    /// Deleting a count removes every pin carrying its id and nothing else, in ONE undoable
    /// step; the surviving count renumbers from its own start and is relabelled Count 1,
    /// because labels follow age among what is left.
    /// </summary>
    [Fact]
    public void DeletingACountTakesOnlyItsOwnPinsAndUndoesInOneStep()
    {
        int afterDelete = 0, afterUndo = 0;
        string[] survivingLabels = [];
        int[] survivingNumbers = [];
        bool ran = false;

        WithEditor((editor, arm, drop, meta) =>
        {
            arm(EditorTool.Step);
            drop(120, 120); drop(200, 120); drop(280, 120);
            string? first = meta().Select(m => m.SeriesId).First();
            Invoke(editor, "NewCounterSeries");
            drop(120, 300); drop(200, 300);

            Invoke(editor, "DeleteCounterSeries", first!);
            afterDelete = meta().Count();
            survivingLabels = CounterSeries.List(meta()).Select(r => r.Label).ToArray();
            survivingNumbers = meta().Select(m => m.Number ?? 0).ToArray();

            UndoOnce(editor);
            afterUndo = meta().Count();
            ran = true;
        });

        if (!ran) return;
        Assert.Equal(2, afterDelete);
        Assert.Equal(new[] { "Count 1" }, survivingLabels);
        Assert.Equal(new[] { 1, 2 }, survivingNumbers);
        Assert.Equal(5, afterUndo);
    }

    /// <summary>
    /// The Start field sets the ACTIVE count's first number, restamps its pins and renumbers —
    /// one undoable step. Survey derives seriesStart from the earliest pin and disables the
    /// field past one pin; WinShot keeps it editable, which is the superset it already shipped.
    /// </summary>
    [Fact]
    public void StartSetsTheActiveCountsFirstNumberAndUndoes()
    {
        int[] shifted = [], restored = [];
        bool ran = false;

        WithEditor((editor, arm, drop, meta) =>
        {
            arm(EditorTool.Step);
            drop(120, 120); drop(200, 120);
            Invoke(editor, "SetCounterSeriesStart", 10);
            shifted = meta().Select(m => m.Number ?? 0).ToArray();
            UndoOnce(editor);
            restored = meta().Select(m => m.Number ?? 0).ToArray();
            ran = true;
        });

        if (!ran) return;
        Assert.Equal(new[] { 10, 11 }, shifted);
        Assert.Equal(new[] { 1, 2 }, restored);
    }

    // ------------------------------------------------------------------ harness

    private static FieldInfo Field(string name) =>
        typeof(EditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Invoke(EditorWindow editor, string method, params object[] args)
    {
        var m = typeof(EditorWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        m!.Invoke(editor, args);
        Pump();
    }

    /// <summary>
    /// One undo through the editor's real entry point — the method is UndoAsync, not Undo, and
    /// reflecting for the short name silently finds nothing.
    /// </summary>
    private static void UndoOnce(EditorWindow editor)
    {
        var m = typeof(EditorWindow).GetMethod("UndoAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        Assert.NotNull(m);
        var task = (Task)m!.Invoke(editor, null)!;
        for (int i = 0; i < 200 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "UndoAsync did not complete");
    }

    private static void WithEditor(
        Action<EditorWindow, Action<EditorTool>, Action<double, double>, Func<IEnumerable<AnnotationData>>> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Environment.GetEnvironmentVariable("WINSHOT_EDITOR_TESTS") != "1") return;
                if (Application.Current is { } inherited && !inherited.CheckAccess()) return;
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var bmp = new SD.Bitmap(1200, 800);
                using (var g = SD.Graphics.FromImage(bmp)) g.Clear(SD.Color.White);

                var editor = new EditorWindow(bmp, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000, Top = -6000, Width = 1400, Height = 900, ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 25; i++) { Pump(); Thread.Sleep(15); }
                editor.UpdateLayout();
                Pump();

                var t = typeof(EditorWindow);
                var toolField = Field("_tool");
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Arm(EditorTool tool)
                {
                    toolField.SetValue(editor, tool);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    for (int i = 0; i < 6; i++) Pump();
                    editor.UpdateLayout();
                    Pump();
                }

                var canvas = (FrameworkElement)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;
                // A counter is placed press-drag-release (PDFViewer.jsx:31727-31790), so a
                // plain press and release on the spot is how the user drops one.
                void Drop(double x, double y)
                {
                    var c = new Point(x, y);
                    var args = new EditorWindow.PointerArgs(
                        c, canvas.TranslatePoint(c, viewport), System.Windows.Input.MouseButton.Left, 1);
                    editor.PointerDown(args);
                    Pump();
                    editor.PointerUp(args);
                    Pump();
                }

                IEnumerable<AnnotationData> Pins() =>
                    ((System.Windows.Controls.Panel)canvas).Children.OfType<Grid>()
                        .Select(g => g.Tag as AnnotationData)
                        .Where(m => m is { Type: AnnotationData.TypeStep })
                        .Select(m => m!)
                        .ToArray();

                body(editor, Arm, Drop, Pins);
                editor.Close();
                Pump();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
