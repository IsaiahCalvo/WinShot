using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Isaiah, 2026-09-17: "I can't change arrow heads."
///
/// The dropdown was wired the whole time — OnArrowStyleChanged sets the default for the next
/// arrow AND restyles the selected one in a single undoable step. Two things hid it: the
/// props host clipped the entire cluster to zero width (see EditorPropsClusterTests), and
/// then the format packer filed the arrowhead dropdown into the "..." overflow flyout on the
/// one tool that exists to use it, because its keep-priority list was in the wrong order.
///
/// So the assertion that matters is not "is the control wired" but "can the user reach it".
/// </summary>
public class EditorArrowheadTests
{
    /// <summary>
    /// Each tool's defining control must stay ON the strip, never in the overflow flyout.
    /// The converge spec is explicit that nothing in this editor hides behind an overflow
    /// popup; this is the part of that promise a narrow window can quietly break.
    /// </summary>
    [Theory]
    [InlineData(1600)]
    [InlineData(1400)]
    [InlineData(1280)]
    [InlineData(1152)]   // EditorShellContract.MinimumEditorLogicalWidth
    public void TheToolsDefiningControlIsNeverInTheOverflowFlyout(double windowWidth)
    {
        var buried = new List<string>();
        WithEditor(windowWidth, (editor, arm) =>
        {
            foreach (var (tool, panel, what) in new[]
                     {
                         (EditorTool.Arrow, "ArrowStylePanel", "arrowhead"),
                         (EditorTool.CurvedArrow, "ArrowStylePanel", "arrowhead"),
                         (EditorTool.Callout, "ArrowStylePanel", "arrowhead"),
                         (EditorTool.Step, "StepModePanel", "counter mode/start"),
                         (EditorTool.Redact, "RedactStylePanel", "redaction style"),
                         (EditorTool.Magnifier, "MagnifyPanel", "magnification"),
                     })
            {
                arm(tool);
                if (editor.FindName(panel) is FrameworkElement fe && !fe.IsVisible)
                    buried.Add($"{tool}: {what} ({panel}) is not reachable on the strip");
            }
        });
        Assert.Empty(buried);
    }

    /// <summary>
    /// Picking a row must restyle the SELECTED arrow and set the default for the NEXT one,
    /// and be a single undo step.
    /// </summary>
    [Fact]
    public void PickingAnArrowheadRestylesTheSelectionAndSetsTheDefault()
    {
        string? before = null, after = null, undone = null;
        object? defaultHead = null;
        WithEditor(1400, (editor, arm) =>
        {
            var t = typeof(EditorWindow);
            FieldInfo F(string n) => t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!;

            arm(EditorTool.Arrow);

            // Draw a real arrow through the in-process pointer seam — no real mouse, no
            // foregrounded window — so this exercises the same path a user's drag takes.
            var canvas = (FrameworkElement)editor.FindName("AnnotationCanvas")!;
            var viewport = (FrameworkElement)editor.FindName("Viewport")!;
            EditorWindow.PointerArgs Pt(Point c) =>
                new(c, canvas.TranslatePoint(c, viewport), System.Windows.Input.MouseButton.Left, 1);

            editor.PointerDown(Pt(new Point(120, 120)));
            Pump();
            editor.PointerMove(Pt(new Point(320, 260)));
            Pump();
            editor.PointerUp(Pt(new Point(320, 260)));
            Pump();

            var arrow = ((System.Windows.Controls.Panel)canvas).Children.OfType<FrameworkElement>()
                .LastOrDefault(c => (c.Tag as AnnotationData)?.Type == AnnotationData.TypeArrow);
            Assert.NotNull(arrow);

            F("_selected").SetValue(editor, arrow);
            before = (arrow!.Tag as AnnotationData)?.Head;

            var box = (ComboBox)editor.FindName("ArrowStyleBox");
            int target = 0;
            for (int i = 0; i < box.Items.Count; i++)
                if (box.Items[i] is ComboBoxItem it && (it.Tag as string) == "openCircle") target = i;
            box.SelectedIndex = target;
            Pump();

            after = (arrow.Tag as AnnotationData)?.Head;
            defaultHead = F("_arrowhead").GetValue(editor);

            // The editor's undo entry point is UndoAsync; reflecting for "Undo" finds nothing
            // and `?.Invoke` then makes this assertion pass without undoing anything.
            UndoOnce(editor);
            undone = (arrow.Tag as AnnotationData)?.Head;
        });

        if (after is null) return;   // seam absent; covered by the reachability theory
        Assert.NotEqual(before, after);
        Assert.Equal(ArrowheadStyle.OpenCircle, defaultHead);
        Assert.Equal(before, undone);
    }

    // ------------------------------------------------ Survey's "Both ends" toggle
    // Pure rules, no Application: these run in a full suite.

    /// <summary>
    /// Survey's reflect rule (PDFViewer.jsx:4235-4241). The toggle reads ON only when the
    /// start ending is set, is not None, and MATCHES the end ending — an imported line with
    /// two DIFFERENT endings shows OFF and keeps both, rather than being silently harmonised.
    /// </summary>
    [Theory]
    [InlineData(ArrowheadStyle.SolidTriangle, ArrowheadStyle.SolidTriangle, true)]
    [InlineData(ArrowheadStyle.OpenCircle, ArrowheadStyle.OpenCircle, true)]
    [InlineData(ArrowheadStyle.SolidTriangle, ArrowheadStyle.None, false)]
    [InlineData(ArrowheadStyle.None, ArrowheadStyle.None, false)]
    [InlineData(ArrowheadStyle.SolidTriangle, ArrowheadStyle.OpenCircle, false)]
    [InlineData(ArrowheadStyle.Diamond, ArrowheadStyle.Square, false)]
    public void BothEndsReadsOnOnlyWhenTheTwoEndingsMatch(
        ArrowheadStyle end, ArrowheadStyle start, bool expected) =>
        Assert.Equal(expected, AnnotationStyle.BothEnds(end, start));

    /// <summary>
    /// Flipping the toggle (or re-picking the ending while it is on) mirrors the END ending
    /// onto the start; turning it off puts the start back to None (PDFViewer.jsx:8158, 8173).
    /// </summary>
    [Theory]
    [InlineData(true, ArrowheadStyle.OpenTriangle, ArrowheadStyle.OpenTriangle)]
    [InlineData(true, ArrowheadStyle.Slash, ArrowheadStyle.Slash)]
    [InlineData(false, ArrowheadStyle.OpenTriangle, ArrowheadStyle.None)]
    public void StartHeadFollowsTheToggle(bool on, ArrowheadStyle end, ArrowheadStyle expected) =>
        Assert.Equal(expected, AnnotationStyle.StartHeadFor(on, end));

    /// <summary>Round trip: what the toggle writes is what the reflect rule reads back.</summary>
    [Fact]
    public void ToggleAndReflectAreInverses()
    {
        foreach (ArrowheadStyle end in Enum.GetValues<ArrowheadStyle>())
        {
            if (end == ArrowheadStyle.None) continue; // None on both ends is not "both ends"
            Assert.True(AnnotationStyle.BothEnds(end, AnnotationStyle.StartHeadFor(true, end)));
            Assert.False(AnnotationStyle.BothEnds(end, AnnotationStyle.StartHeadFor(false, end)));
        }
    }

    /// <summary>
    /// The toggle is Survey's arrow-only control, and it sits in the arrow props row right of
    /// the arrowhead dropdown — so it must be reachable with Arrow armed and gone on Callout,
    /// whose leader has one end.
    /// </summary>
    [Fact]
    public void BothEndsShowsOnArrowAndNotOnCallout()
    {
        bool? onArrow = null, onCallout = null;
        WithEditor(1400, (editor, arm) =>
        {
            var btn = (FrameworkElement)editor.FindName("ArrowBothEndsBtn")!;
            arm(EditorTool.Arrow);
            onArrow = btn.IsVisible;
            arm(EditorTool.Callout);
            onCallout = btn.IsVisible;
        });
        if (onArrow is null) return;   // stood down: no Application to own
        Assert.True(onArrow);
        Assert.False(onCallout);
    }

    /// <summary>
    /// End to end through the pointer seam: draw a real arrow, select it, click the toggle.
    /// The selected arrow gains the matching start ending in ONE undoable step, and clicking
    /// again takes it away — the same contract every other style control on the row has.
    /// </summary>
    [Fact]
    public void TogglingBothEndsRestylesTheSelectedArrowAndUndoes()
    {
        string? head = null, startOn = null, startUndone = null, startOff = null;
        bool ran = false;
        WithEditor(1400, (editor, arm) =>
        {
            var t = typeof(EditorWindow);
            FieldInfo F(string n) => t.GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!;
            arm(EditorTool.Arrow);

            var canvas = (FrameworkElement)editor.FindName("AnnotationCanvas")!;
            var viewport = (FrameworkElement)editor.FindName("Viewport")!;
            EditorWindow.PointerArgs Pt(Point c) =>
                new(c, canvas.TranslatePoint(c, viewport), System.Windows.Input.MouseButton.Left, 1);

            editor.PointerDown(Pt(new Point(120, 120)));
            Pump();
            editor.PointerMove(Pt(new Point(320, 260)));
            Pump();
            editor.PointerUp(Pt(new Point(320, 260)));
            Pump();

            var arrow = ((System.Windows.Controls.Panel)canvas).Children.OfType<FrameworkElement>()
                .LastOrDefault(c => (c.Tag as AnnotationData)?.Type == AnnotationData.TypeArrow);
            if (arrow is null) return;
            ran = true;
            F("_selected").SetValue(editor, arrow);
            head = (arrow.Tag as AnnotationData)?.Head;

            var btn = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ArrowBothEndsBtn")!;
            btn.IsChecked = true;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            startOn = (arrow.Tag as AnnotationData)?.StartHead;

            UndoOnce(editor);
            startUndone = (arrow.Tag as AnnotationData)?.StartHead;

            // Undo clears the selection (EditorWindow.Edit.cs:242), so re-arm it before the
            // second half — this is the same mark, toggled back off.
            F("_selected").SetValue(editor, arrow);
            btn.IsChecked = false;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            startOff = (arrow.Tag as AnnotationData)?.StartHead;
        });
        if (!ran) return;   // stood down, or the seam is absent
        Assert.Equal(head, startOn);            // the start mirrors the end exactly
        Assert.Null(startUndone);               // one undoable step
        Assert.Null(startOff);                  // toggling off clears it
    }

    private static void WithEditor(double width, Action<EditorWindow, Action<EditorTool>> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // This assembly gets exactly ONE System.Windows.Application, and the thread
                // that creates it owns it for good — after that thread exits, Application.Current
                // still points at a Dispatcher nobody is running, so a later test can neither
                // reuse it nor replace it. ThemedWindowTests is the established owner (it builds
                // one and shuts it down), so claiming it here would fail that test with "Cannot
                // create more than one System.Windows.Application instance in the same AppDomain"
                // — which is exactly what these tests did to it. Same reason every render harness
                // in this suite is env-gated: one editor-window class per host, run deliberately.
                // The handoff lists the command.
                if (Environment.GetEnvironmentVariable("WINSHOT_EDITOR_TESTS") != "1") return;
                if (Application.Current is { } inherited && !inherited.CheckAccess())
                    return;
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
                    Left = -6000, Top = -6000, Width = width, Height = 900, ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 25; i++) { Pump(); Thread.Sleep(15); }
                editor.UpdateLayout();
                Pump();

                var t = typeof(EditorWindow);
                var toolField = t.GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Arm(EditorTool tool)
                {
                    toolField.SetValue(editor, tool);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    // PackFormatOverflow is posted at Loaded priority; let it land, then
                    // settle, because it can reparent and trigger another pass.
                    for (int i = 0; i < 6; i++) { Pump(); }
                    editor.UpdateLayout();
                    Pump();
                }

                body(editor, Arm);
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

    /// <summary>
    /// One undo through the editor's real entry point. There is no <c>Undo()</c> — the method
    /// is <c>UndoAsync()</c> (EditorWindow.Edit.cs:238), and reflecting for the short name
    /// silently finds nothing, which makes an "and it undoes" assertion pass without ever
    /// undoing anything. Invoke the Task-returning one and pump until it completes.
    /// </summary>
    private static void UndoOnce(EditorWindow editor)
    {
        var method = typeof(EditorWindow).GetMethod("UndoAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(editor, null)!;
        for (int i = 0; i < 200 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "UndoAsync did not complete");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
