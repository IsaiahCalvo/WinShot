using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The props/format cluster has to actually PAINT, not merely lay out.
///
/// Isaiah, 2026-09-17: "When I select an annotation tool I don't have any formatting options
/// to change line width or colour." The cause was the props host carrying an explicit
/// <c>Width="0"</c>. A horizontal StackPanel's ArrangeOverride grows its arrange size to the
/// measured extent of its children; ArrangeCore then sees that ink size exceed the explicit
/// Width and applies a zero-width LAYOUT CLIP. Every control still measured, still arranged
/// and still answered <c>IsVisible == true</c> — which is exactly why reading the visual tree
/// could not see the bug. Only counting ink can, so that is what this asserts.
/// </summary>
public class EditorPropsClusterTests
{
    [Fact]
    public void FormatControlsPaint_ForEveryToolThatOwnsThem()
    {
        var (ink, spread, missing) = Probe();
        // Stood down because another test owns the Application — see Probe.
        if (ink.Count == 0) return;

        // Every tool whose contract offers colour or thickness must put real ink on the strip.
        Assert.Empty(missing);

        // A tool with no contextual controls at all (Pan) paints nothing there; a tool with
        // them paints a lot. The gap is three orders of magnitude, so the bar is not delicate.
        Assert.True(ink[EditorTool.Rectangle] > 1000,
            $"Rectangle put {ink[EditorTool.Rectangle]} ink px right of the tool cluster; " +
            "the props host is clipping again.");
        Assert.True(ink[EditorTool.Pan] < 200,
            $"Pan put {ink[EditorTool.Pan]} ink px right of the tool cluster; it has no " +
            "contextual controls and should leave the strip empty.");

        // Survey's promise: the centred tool row never moves as the armed tool changes.
        // This is what the zero-width host was meant to protect and did not need to.
        Assert.True(spread < 0.01, $"the centred tool row moved {spread:F3} DIP between tools.");
    }

    /// <returns>ink per tool, the tool row's x spread, and the tools that painted nothing.</returns>
    private static (Dictionary<EditorTool, int> Ink, double Spread, List<EditorTool> Missing) Probe()
    {
        Exception? failure = null;
        var ink = new Dictionary<EditorTool, int>();
        var missing = new List<EditorTool>();
        double spread = 0;

        var thread = new Thread(() =>
        {
            try
            {
                // This assembly gets exactly ONE System.Windows.Application and whichever test
                // reaches it first owns it on its own STA thread. Touching it from here would
                // throw "the calling thread cannot access this object", and building an
                // EditorWindow against a dispatcher that is not running lays out windows that
                // never resolve their theme. So stand down when someone else owns it — the
                // full suite stays green — and do the real work when run under a filter, which
                // is how the handoff says to run it. OnExplicitShutdown because the default
                // OnLastWindowClose would shut the Application down when this test closes its
                // window, poisoning every later test in the host.
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
                    Left = -6000,
                    Top = -6000,
                    Width = 1400,
                    Height = 900,
                    ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 25; i++) { Pump(); Thread.Sleep(15); }
                editor.UpdateLayout();
                Pump();

                var t = typeof(EditorWindow);
                var toolField = t.GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var bar = (FrameworkElement)editor.FindName("TopToolbar");
                var tools = (FrameworkElement)editor.FindName("ToolPanel");
                var xs = new List<double>();

                foreach (EditorTool tool in Enum.GetValues<EditorTool>())
                {
                    toolField.SetValue(editor, tool);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    for (int i = 0; i < 4; i++) Pump();
                    editor.UpdateLayout();
                    Pump();

                    xs.Add(tools.TransformToAncestor(editor).Transform(new Point(0, 0)).X);
                    int count = InkRightOfTools(bar, tools);
                    ink[tool] = count;

                    var controls = EditorShellContract.ContextFor(tool, false);
                    bool owed = controls.HasFlag(EditorContextControls.Color)
                                || controls.HasFlag(EditorContextControls.Thickness)
                                || controls.HasFlag(EditorContextControls.RedactStyle)
                                || controls.HasFlag(EditorContextControls.CropRatio);
                    if (owed && count < 500) missing.Add(tool);
                }

                spread = xs.Max() - xs.Min();
                editor.Close();
                Pump();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return (ink, spread, missing);
    }

    /// <summary>
    /// Renders the toolbar and counts pixels right of the tool cluster that are not the
    /// most common colour in that band (its flat chrome fill).
    /// </summary>
    private static int InkRightOfTools(FrameworkElement bar, FrameworkElement tools)
    {
        int w = (int)Math.Ceiling(bar.ActualWidth), h = (int)Math.Ceiling(bar.ActualHeight);
        if (w <= 0 || h <= 0) return 0;
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(bar);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);

        int from = (int)(tools.TransformToAncestor(bar).Transform(new Point(0, 0)).X
                         + tools.ActualWidth + 8);
        from = Math.Clamp(from, 0, w);
        // Count only pixels the element actually PAINTED. The bitmap is sized with
        // Math.Ceiling, so a fractional ActualWidth/Height leaves a strip of untouched
        // pixels along the right and bottom edges; those come back fully transparent and
        // read as #000000, which an RGB-only comparison happily scores as ink. Measured
        // with Pan armed (no contextual controls at all): one stray black pixel on every
        // row from the last column, plus three whole rows past the element's height — 629
        // px of pure measurement error. Alpha is what separates painted from never-touched.
        // Count only FULLY OPAQUE pixels — that is what separates controls from frame here.
        // Two things in this band are not controls and both are partially transparent:
        // the bitmap is sized with Math.Ceiling, so a fractional ActualWidth/Height leaves
        // untouched pixels (alpha 0) that an RGB-only test scores as black ink; and the
        // toolbar's own antialiased bottom edge plus its Border hairline (#1FFFFFFF, a
        // 12%-white wash over the uncovered border strip) land at low alpha and, being
        // PREMULTIPLIED in Pbgra32, read DARKER than the background rather than lighter.
        // Measured with Pan armed, which has no contextual controls at all: 1 stray pixel
        // per row from the last column, 3 whole rows past the element's height, and rows
        // 51-52 at #201F1F/#1F1F1F against a #282626 background — 1158 px of pure
        // measurement error. Anything the cluster actually draws sits on the bar's opaque
        // fill and comes back at alpha 255.
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < h; y++)
            for (int x = from; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] != 255) continue;             // Pbgra32: B,G,R,A
                int key = (px[i] << 16) | (px[i + 1] << 8) | px[i + 2];
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        if (counts.Count == 0) return 0;
        int bg = counts.OrderByDescending(kv => kv.Value).First().Key;
        return counts.Where(kv => kv.Key != bg).Sum(kv => kv.Value);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
