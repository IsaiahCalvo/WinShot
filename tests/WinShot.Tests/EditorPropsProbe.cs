using System.IO;
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
/// AUDIT-ONLY probe (WINSHOT_RENDER_PROPS=1): arms every tool in turn and dumps the real
/// layout of the props/format cluster — visibility, ActualWidth, and the position of each
/// element in WINDOW coordinates — so "the formatting options do not appear" can be traced
/// to the element that actually loses them instead of guessed at.
/// </summary>
public class EditorPropsProbe
{
    [Fact]
    public void ProbePropsClusterLayout()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_PROPS") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-props-probe");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var lines = new List<string>();

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
                    Left = -6000,
                    Top = -6000,
                    Width = 1400,
                    Height = 900,
                    ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 30; i++) { Pump(); Thread.Sleep(20); }
                editor.UpdateLayout();
                Pump();

                var t = typeof(EditorWindow);
                var toolField = t.GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var chromeTool = t.GetMethod("ChromeTool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var slackM = t.GetMethod("MeasureFormatSlack", BindingFlags.Instance | BindingFlags.NonPublic)!;

                // --- WIDTH SWEEP through the REAL click path -------------------------------
                // The direct UpdateContextPanels call and a real group-button click are two
                // different code paths; the defect only shows on one of them, so exercise both.
                lines.Add("########## WIDTH SWEEP: real Shapes-group click, Rectangle armed ##########");
                foreach (double w in new[] { 1600.0, 1400, 1280, 1200, 1152, 1100, 1000 })
                {
                    editor.Width = w;
                    editor.UpdateLayout();
                    Pump();
                    if (editor.FindName("ShapeGroupBtn") is System.Windows.Controls.Button gb)
                        gb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(15); }
                    editor.UpdateLayout();
                    Pump();

                    double slack = (double)slackM.Invoke(editor, null)!;
                    lines.Add($"=== window={w} slack={slack:F1} tool={toolField.GetValue(editor)}");
                    foreach (string n in new[]
                             {
                                 "TopToolbar", "ToolPanel", "SecondaryToolPanel", "ColorPanel",
                                 "SizeEffectHost", "ThicknessPanel", "EditorFormatBar",
                                 "ContextPanel", "LineStylePanel", "FormatOverflowBtn",
                                 "FormatOverflowPanel",
                             })
                    {
                        if (editor.FindName(n) is not FrameworkElement fe) continue;
                        string pos = "n/a";
                        try
                        {
                            if (fe.IsVisible)
                                pos = $"x={fe.TransformToAncestor(editor).Transform(new Point(0, 0)).X:F1}";
                        }
                        catch (InvalidOperationException) { pos = "<detached>"; }
                        string parent = (VisualTreeHelper.GetParent(fe) as FrameworkElement)?.Name ?? "?";
                        lines.Add($"    {n,-20} vis={fe.Visibility,-9} isVis={fe.IsVisible,-5} " +
                                  $"w={fe.ActualWidth,7:F1} {pos,-12} parent={parent}");
                    }
                }
                // --- PAINT TEST -------------------------------------------------------------
                // Layout says the cluster is placed and IsVisible; the shipped render says the
                // strip is empty right of the tools. IsVisible ignores clipping, so render the
                // toolbar and COUNT ink to the right of the tool cluster. Then flip the
                // zero-width props host to Auto and render again: if ink appears, the
                // `Width="0"` host is what eats the controls.
                editor.Width = 1152;
                editor.UpdateLayout();
                Pump();
                var bar = (FrameworkElement)editor.FindName("TopToolbar");
                var tools = (FrameworkElement)editor.FindName("ToolPanel");

                int InkRightOfTools(string png)
                {
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    rtb.Render(bar);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(outDir, png))) enc.Save(fs);

                    int w = rtb.PixelWidth, h = rtb.PixelHeight;
                    var px = new byte[w * h * 4];
                    rtb.CopyPixels(px, w * 4, 0);
                    double toolsRight = tools.TransformToAncestor(bar).Transform(new Point(0, 0)).X
                                        + tools.ActualWidth + 8;
                    // Background is the flat chrome fill; count pixels that differ from the
                    // most common colour in that band.
                    var counts = new Dictionary<int, int>();
                    for (int y = 0; y < h; y++)
                        for (int x = (int)toolsRight; x < w; x++)
                        {
                            int i = (y * w + x) * 4;
                            int key = (px[i] << 16) | (px[i + 1] << 8) | px[i + 2];
                            counts[key] = counts.GetValueOrDefault(key) + 1;
                        }
                    if (counts.Count == 0) return 0;
                    int bg = counts.OrderByDescending(kv => kv.Value).First().Key;
                    return counts.Where(kv => kv.Key != bg).Sum(kv => kv.Value);
                }

                // Per-row ink profile with PAN armed. Pan has no contextual controls at all,
                // so anything painting right of the tool cluster is chrome, and this says
                // which ROWS it is on — the alternative is guessing at a threshold.
                // Which group click moves the centred tool row, and what changes with it?
                lines.Add("########## GROUP-CLICK ROW STABILITY ##########");
                {
                    editor.Width = 1400; editor.UpdateLayout(); Pump();
                    var row = (System.Windows.Controls.Grid)editor.FindName("ToolbarPrimaryRow");
                    var toolsP = (FrameworkElement)editor.FindName("ToolPanel");
                    var panSel = (FrameworkElement)editor.FindName("PanSelectPanel");
                    var sec = (FrameworkElement)editor.FindName("SecondaryToolPanel");
                    void Dump(string tag)
                    {
                        editor.UpdateLayout();
                        for (int i = 0; i < 6; i++) Pump();
                        editor.UpdateLayout(); Pump();
                        string cols = string.Join(" | ", row.ColumnDefinitions.Select(c => $"{c.ActualWidth:F2}"));
                        lines.Add($"    {tag,-16} toolX={toolsP.TransformToAncestor(editor).Transform(new Point(0,0)).X,8:F2}" +
                                  $" toolW={toolsP.ActualWidth,7:F2} cols=[{cols}]" +
                                  $" panSelW={panSel.ActualWidth:F2} secW={sec.ActualWidth:F2}" +
                                  $" rowW={row.ActualWidth:F2}");
                    }
                    Dump("baseline");
                    foreach (string bn in new[]{"DrawGroupBtn","ShapeGroupBtn","TextGroupBtn","EffectsGroupBtn","ImageGroupBtn"})
                        if (editor.FindName(bn) is System.Windows.Controls.Button gb2)
                        {
                            gb2.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            Dump(bn);
                        }
                }

                lines.Add("########## PAN ROW PROFILE (window=1152) ##########");
                {
                    toolField.SetValue(editor, EditorTool.Pan);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    for (int i = 0; i < 5; i++) Pump();
                    editor.UpdateLayout(); Pump();

                    int bw = (int)Math.Ceiling(bar.ActualWidth), bh = (int)Math.Ceiling(bar.ActualHeight);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        bw, bh, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(bar);
                    var px2 = new byte[bw * bh * 4];
                    rtb.CopyPixels(px2, bw * 4, 0);
                    int x0 = (int)(tools.TransformToAncestor(bar).Transform(new Point(0, 0)).X
                                   + tools.ActualWidth + 8);
                    var tally = new Dictionary<int, int>();
                    for (int y = 0; y < bh; y++)
                        for (int x = x0; x < bw; x++)
                        {
                            int i = (y * bw + x) * 4;
                            if (px2[i + 3] == 0) continue;
                            int k = (px2[i] << 16) | (px2[i + 1] << 8) | px2[i + 2];
                            tally[k] = tally.GetValueOrDefault(k) + 1;
                        }
                    int bg2 = tally.OrderByDescending(kv => kv.Value).First().Key;
                    lines.Add($"    bar={bw}x{bh}  actual={bar.ActualWidth:F2}x{bar.ActualHeight:F2}" +
                              $"  borderBottom={(bar as System.Windows.Controls.Border)?.BorderThickness.Bottom}" +
                              $"  band x{x0}..{bw}  bg=#{bg2:X6}");
                    for (int y = 0; y < bh; y++)
                    {
                        int n = 0; int sample = bg2;
                        for (int x = x0; x < bw; x++)
                        {
                            int i = (y * bw + x) * 4;
                            if (px2[i + 3] == 0) continue;
                            int k = (px2[i] << 16) | (px2[i + 1] << 8) | px2[i + 2];
                            if (k != bg2) { n++; sample = k; }
                        }
                        if (n > 0) lines.Add($"    row {y,3}: {n,5} non-bg px   e.g. #{sample:X6}");
                    }
                }

                lines.Add("########## PAINT TEST (window=1152, Rectangle armed) ##########");
                lines.Add($"    ink right of tools, AS SHIPPED          = {InkRightOfTools("paint-asshipped.png")}");

                // The props host is the unnamed StackPanel parent of SecondaryToolPanel.
                var secondary = (FrameworkElement)editor.FindName("SecondaryToolPanel");
                var host = (FrameworkElement)VisualTreeHelper.GetParent(secondary);
                lines.Add($"    props host: type={host.GetType().Name} Width={host.Width} " +
                          $"ActualWidth={host.ActualWidth} ClipToBounds={host.ClipToBounds} " +
                          $"HAlign={host.HorizontalAlignment}");
                host.Width = double.NaN; // Auto
                editor.UpdateLayout();
                Pump();
                lines.Add($"    ink right of tools, host Width=Auto     = {InkRightOfTools("paint-hostauto.png")}");
                lines.Add($"    ToolPanel x with host Auto              = " +
                          $"{tools.TransformToAncestor(editor).Transform(new Point(0, 0)).X:F2}");
                host.Width = 0;
                editor.UpdateLayout();
                Pump();
                lines.Add($"    ToolPanel x with host Width=0           = " +
                          $"{tools.TransformToAncestor(editor).Transform(new Point(0, 0)).X:F2}");

                // --- ANCHOR TEST ------------------------------------------------------------
                // The zero-width host existed to stop a wide tool shoving the centred cluster.
                // Both side columns are `*`, so the Grid centres the Auto column by itself.
                // Prove it: with the host at Auto, sweep every tool at every width and record
                // the cluster's x. If it never moves, `Width="0"` was buying nothing.
                lines.Add("########## ANCHOR TEST: host Width=Auto, does the tool row move? ##########");
                foreach (double w in new[] { 1600.0, 1400, 1280, 1152, 1000 })
                {
                    editor.Width = w;
                    var xs = new List<double>();
                    foreach (EditorTool tool in Enum.GetValues<EditorTool>())
                    {
                        foreach (double hw in new[] { 0.0, double.NaN })
                        {
                            host.Width = hw;
                            toolField.SetValue(editor, tool);
                            update.Invoke(editor, null);
                            editor.UpdateLayout();
                            Pump();
                            for (int i = 0; i < 3; i++) { Pump(); }
                            editor.UpdateLayout();
                            Pump();
                            if (double.IsNaN(hw))
                                xs.Add(tools.TransformToAncestor(editor).Transform(new Point(0, 0)).X);
                        }
                    }
                    lines.Add($"    window={w,6}  toolrow x: min={xs.Min():F2} max={xs.Max():F2} " +
                              $"spread={xs.Max() - xs.Min():F3} over {xs.Count} tools");
                }
                host.Width = 0;

                editor.Width = 1400;
                editor.UpdateLayout();
                Pump();
                lines.Add("########## DIRECT UpdateContextPanels, per tool, window=1400 ##########");

                string[] names =
                {
                    "TopToolbar", "ToolbarPrimaryRow", "ToolPanel", "PanSelectPanel",
                    "SecondaryToolPanel", "EditorColorBar", "ColorPanel", "ColorWellBtn",
                    "SizeEffectHost", "ThicknessPanel", "EditorFormatBar", "ContextPanel",
                    "ArrowStylePanel", "LineStylePanel", "StepModePanel", "StepStartInput",
                    "FormatOverflowBtn", "FormatOverflowPanel", "CropPanel",
                };

                foreach (EditorTool tool in new[]
                         {
                             EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Line,
                             EditorTool.Arrow, EditorTool.Freehand, EditorTool.Highlighter,
                             EditorTool.Text, EditorTool.Callout, EditorTool.Step,
                             EditorTool.Eraser, EditorTool.Redact, EditorTool.Magnifier,
                             EditorTool.Select,
                         })
                {
                    toolField.SetValue(editor, tool);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    Pump();
                    // PackFormatOverflow is posted at Loaded priority; let it land.
                    for (int i = 0; i < 5; i++) { Pump(); Thread.Sleep(10); }
                    editor.UpdateLayout();
                    Pump();

                    var chrome = chromeTool.Invoke(editor, null);
                    var controls = EditorShellContract.ContextFor((EditorTool)chrome!, false);
                    lines.Add($"=== tool={tool} chrome={chrome} controls={controls}");

                    foreach (string n in names)
                    {
                        if (editor.FindName(n) is not FrameworkElement fe)
                        {
                            lines.Add($"    {n,-22} <not found>");
                            continue;
                        }
                        string pos = "n/a";
                        try
                        {
                            if (fe.IsVisible)
                            {
                                Point p = fe.TransformToAncestor(editor).Transform(new Point(0, 0));
                                pos = $"x={p.X:F1} y={p.Y:F1}";
                            }
                        }
                        catch (InvalidOperationException) { pos = "<not connected>"; }

                        string parent = (VisualTreeHelper.GetParent(fe) as FrameworkElement)?.Name ?? "?";
                        lines.Add(
                            $"    {n,-22} vis={fe.Visibility,-9} isVis={fe.IsVisible,-5} " +
                            $"w={fe.ActualWidth,7:F1} h={fe.ActualHeight,6:F1} desW={fe.DesiredSize.Width,7:F1} " +
                            $"{pos,-22} parent={parent}");
                    }
                }

                File.WriteAllLines(Path.Combine(outDir, "props-probe.txt"), lines);
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
