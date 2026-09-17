using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// AUDIT-ONLY probe (WINSHOT_RENDER_FIT=1). Isaiah: "Fit-to-page and how the page fits is
/// weird." The arithmetic already matches Survey (PAD 20 per side, GAP 16 added to the
/// height divisor, fit page = min of the two), so this dumps the INPUTS instead: what the
/// editor measures as its viewport, what it actually zooms to, and how much of the picture
/// the 34 DIP group band is covering — because Survey's scroller starts BELOW its sub-row
/// (measured on the running app: clientHeight 823 = 900 - 77) while WinShot's band overlays
/// the canvas, so the box the fit is computed against and the box the user can see are not
/// the same box.
/// </summary>
public class EditorFitProbe
{
    [Fact]
    public void ProbeFitGeometry()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_FIT") != "1") return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-fit-probe");
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

                // Three shapes of capture: a wide screenshot, a square-ish one, and a tall
                // scroll capture — fit-page picks a different limiting axis for each.
                foreach (var (sw, sh, label) in new[]
                         {
                             (1920, 1080, "wide 16:9"),
                             (1200, 800, "3:2"),
                             (800, 2400, "tall scroll"),
                             (300, 200, "tiny"),
                         })
                {
                    var bmp = new SD.Bitmap(sw, sh);
                    using (var g = SD.Graphics.FromImage(bmp)) g.Clear(SD.Color.CornflowerBlue);
                    var editor = new EditorWindow(bmp, settings, history)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -6000, Top = -6000, Width = 1400, Height = 900, ShowInTaskbar = false,
                    };
                    editor.Show();
                    for (int i = 0; i < 30; i++) { Pump(); Thread.Sleep(20); }
                    editor.UpdateLayout();
                    Pump();

                    var t = typeof(EditorWindow);
                    double Get(string f) => (double)t.GetField(f,
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    object Mode() => t.GetField("_viewFitMode",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

                    var viewport = (FrameworkElement)editor.FindName("Viewport");
                    var groupBar = (FrameworkElement)editor.FindName("EditorGroupBar");
                    double vw = viewport.ActualWidth, vh = viewport.ActualHeight;
                    double zoom = Get("_zoom");

                    // Survey's formula, applied to the same numbers.
                    double fitW = Math.Max(1, vw - 40) / sw;
                    double fitH = vh / (sh + 32);
                    double expect = Math.Min(fitW, fitH);

                    double bandH = groupBar.Visibility == Visibility.Visible ? groupBar.ActualHeight : 0;
                    double shown = sh * zoom;
                    double topCovered = Math.Max(0, bandH - Math.Max(0, (vh - shown) / 2));

                    lines.Add($"=== {label}  src={sw}x{sh}");
                    lines.Add($"    viewport      = {vw:F1} x {vh:F1}   (window 1400x900)");
                    lines.Add($"    mode          = {Mode()}");
                    lines.Add($"    zoom          = {zoom:F4}  ({zoom * 100:F1}%)");
                    lines.Add($"    survey fitW   = {fitW:F4}   fitH = {fitH:F4}   min = {expect:F4}");
                    lines.Add($"    delta         = {Math.Abs(zoom - expect):F6}" +
                              (Math.Abs(zoom - expect) < 1e-6 ? "  (matches Survey)" : "  <-- DIVERGES"));
                    lines.Add($"    drawn size    = {sw * zoom:F1} x {shown:F1}");
                    lines.Add($"    group band    = {bandH:F1} DIP visible={groupBar.Visibility}" +
                              $" -> covers {topCovered:F1} DIP of the picture");

                    // What Survey would do if the band were excluded from the fit box, the
                    // way Survey's scroller excludes its own sub-row.
                    double vhMinusBand = vh - 34;
                    double fitHNoBand = vhMinusBand / (sh + 32);
                    lines.Add($"    if band excluded from the fit box: fitH = {fitHNoBand:F4}" +
                              $"  min = {Math.Min(fitW, fitHNoBand):F4}" +
                              $"  ({Math.Min(fitW, fitHNoBand) * 100:F1}%)");
                    lines.Add("");

                    editor.Close();
                    Pump();
                    bmp.Dispose();
                }

                File.WriteAllLines(Path.Combine(outDir, "fit-probe.txt"), lines);
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
