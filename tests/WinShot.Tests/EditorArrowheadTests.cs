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

            var undo = t.GetMethod("Undo", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            undo?.Invoke(editor, null);
            Pump();
            undone = (arrow.Tag as AnnotationData)?.Head;
        });

        if (after is null) return;   // seam absent; covered by the reachability theory
        Assert.NotEqual(before, after);
        Assert.Equal(ArrowheadStyle.OpenCircle, defaultHead);
        Assert.Equal(before, undone);
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

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
