using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;
using WpfPath = System.Windows.Shapes.Path;

namespace WinShot.Tests;

/// <summary>
/// What a revision cloud COSTS, per stage, on the sizes Isaiah actually draws.
///
/// The cloud is rebuilt from scratch on every pointer move at six sites (the drag preview, the
/// live drag, the resize, the restyle, the undo snapshot and the factory), and the selection
/// chrome measures and re-widens the whole thing again on top of that. This probe is where
/// those numbers come from, so an optimisation can be compared rather than argued about — and
/// so a future change that quietly puts the per-move rebuild back has something that goes red.
///
/// MEASURE WHAT THE DRAG DOES, not what a warm loop does. Every stage below is timed on a
/// FRESH geometry, because <see cref="Geometry.Bounds"/> and the widened-path caches are
/// memoised per instance: re-timing one geometry reports ~0 ms for work that costs tens of
/// milliseconds every single frame of a real drag, which is exactly the illusion that let this
/// cost hide.
///
/// It reports through <c>WINSHOT_RENDER_OUTPUT</c> like the other probes (a table to stdout and
/// to that directory); the assertion is always on, with a ceiling generous enough to survive a
/// loaded box but tight enough to catch an order of magnitude.
///
/// LANDMINE: the drag case needs a real <see cref="EditorWindow"/>, so it carries the usual
/// one-Application-per-assembly gate and must be run ALONE:
///     $env:WINSHOT_EDITOR_TESTS='1'
///     powershell -File .\tools\run-tests.ps1 -Filter 'FullyQualifiedName~CloudPerfProbe'
/// A stood-down run reports as a PASS.
/// </summary>
public class CloudPerfProbe
{
    /// <summary>The two boxes: a typical mark, and the one Isaiah says bogs the editor down.</summary>
    private static readonly (string Name, Rect Box)[] Boxes =
    [
        ("1200x800", new Rect(0, 0, 1200, 800)),
        ("3000x2000", new Rect(0, 0, 3000, 2000)),
    ];

    private static readonly double[] Bumps = [1, 2, 4];

    private const double Stroke = 4;

    /// <summary>
    /// Per-stage cost of one cloud: the engine's own outline, the WPF geometry over it, the
    /// selection frame, the hover glow, and one real Arrange + raster of the Path carrying it.
    /// </summary>
    [Fact]
    public void CloudStagesStayUnderTheirCeiling()
    {
        var report = new StringBuilder();
        var worst = new List<(string Label, double Ms)>();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                report.AppendLine("stage timings (ms, median of 9 runs on a FRESH geometry each time)");
                report.AppendLine();
                report.AppendLine("| box | bump | Outline | ForBox | SelectionFrame | HoverGlowRing | Arrange+render |");
                report.AppendLine("|---|---|---|---|---|---|---|");

                foreach ((string name, Rect box) in Boxes)
                    foreach (double bump in Bumps)
                    {
                        const string type = AnnotationData.TypeRectangle;
                        Point[] ring = [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];
                        double outline = Median(
                            () => 0,
                            _ => RevisionCloud.Outline(
                                CloudKind.Rectangle, ring, CloudPath.ScallopSize(bump)));
                        double forBox = Median(() => 0, _ => CloudPath.ForBox(type, box, bump, Stroke));
                        double frame = Median(
                            () => CloudPath.ForBox(type, box, bump, Stroke),
                            g => CloudPath.SelectionFrame(g, Stroke));
                        double glow = Median(
                            () => CloudPath.ForBox(type, box, bump, Stroke),
                            g => CloudPath.HoverGlowRing(g, Stroke));
                        double render = Median(
                            () => CloudPath.ForBox(type, box, bump, Stroke),
                            g => RenderOnce(g, box, Stroke), runs: 5);

                        report.AppendLine($"| {name} | {bump} | {outline:F2} | {forBox:F2} | " +
                                          $"{frame:F2} | {glow:F2} | {render:F2} |");
                        worst.Add(($"{name} bump {bump} Outline", outline));
                        worst.Add(($"{name} bump {bump} ForBox", forBox));
                        worst.Add(($"{name} bump {bump} SelectionFrame", frame));
                        worst.Add(($"{name} bump {bump} HoverGlowRing", glow));
                        worst.Add(($"{name} bump {bump} Arrange+render", render));
                    }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());

        Emit("cloud-perf-stages.txt", report.ToString());

        // Generous on purpose: this is an order-of-magnitude tripwire on a shared box, not a
        // budget. A single stage taking a fifth of a second is not a slow machine, it is a
        // rebuilt-the-world regression.
        foreach ((string label, double ms) in worst)
            Assert.True(ms < 200, $"{label} took {ms:F1} ms\n{report}");
    }

    /// <summary>
    /// The thing the user actually feels: 60 pointer moves through the real <c>PointerArgs</c>
    /// seam, drawing a cloud out and then resizing the committed one, as ms/frame p50 / p95.
    /// <para>The RESIZE leg is the expensive one and the reason it is here: it rebuilds the
    /// geometry (<c>ResizeBoxShape</c>) AND re-runs the whole selection chrome
    /// (<c>UpdateSelectionVisual</c> → <c>PaintCloudGlow</c>), which widens every crown twice
    /// and subtracts the two. Timing only the draw-out leg would have missed the larger half.
    /// </para>
    /// </summary>
    [Fact]
    public void ADragOfABigCloudStaysInteractive()
    {
        var report = new StringBuilder();
        var results = new List<(string Label, double P95)>();
        bool ran = false;
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
                var bmp = new SD.Bitmap(3400, 2400);
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
                var toolField = t.GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var styleField = t.GetField("_lineStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var bumpField = t.GetField("_cloudIntensity", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var select = t.GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic,
                    [typeof(UIElement)])!;
                var canvas = (Panel)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;

                report.AppendLine("drag: 60 pointer moves through the PointerArgs seam, ms/frame");
                report.AppendLine("(the move handler plus one layout pass, so the rebuild AND the paint it forces)");
                report.AppendLine();
                report.AppendLine("| leg | box | bump | p50 | p95 | max | total |");
                report.AppendLine("|---|---|---|---|---|---|---|");

                // One throwaway pass so the report is not a JIT trace.
                Drag(LineBorderStyle.Cloud, 1, new Rect(0, 0, 400, 300));

                foreach ((string name, Rect box) in Boxes)
                {
                    foreach (double bump in Bumps)
                    {
                        (double[] draw, double[] resize) = Drag(LineBorderStyle.Cloud, bump, box);
                        Row("draw", name, bump, draw);
                        Row("resize", name, bump, resize);
                    }
                    // Control: the SAME drag on a plain solid rectangle. Everything a cloud
                    // frame does that is not the cloud — the pointer plumbing, the dashed frame,
                    // the eight grabbers, UpdateSelectionVisual, one layout pass — is in here
                    // too, so this row is the floor. A cloud row close to its control row means
                    // the cloud is no longer what costs; chase the chrome instead.
                    (double[] sDraw, double[] sResize) = Drag(LineBorderStyle.Solid, 2, box);
                    Row("draw", name, "solid", sDraw);
                    Row("resize", name, "solid", sResize);
                }

                editor.Close();
                Pump();
                ran = true;

                void Row(string leg, string name, object bump, double[] frames)
                {
                    if (frames.Length == 0) return;
                    double total = 0;
                    foreach (double f in frames) total += f;
                    Array.Sort(frames);
                    double p50 = frames[frames.Length / 2];
                    double p95 = frames[(int)(frames.Length * 0.95)];
                    report.AppendLine($"| {leg} | {name} | {bump} | {p50:F2} | {p95:F2} | " +
                                      $"{frames[^1]:F2} | {total:F0} |");
                    results.Add(($"{leg} {name} bump {bump}", p95));
                }

                (double[] Draw, double[] Resize) Drag(LineBorderStyle style, double bump, Rect box)
                {
                    // Drop the selection FIRST: the handles of the previous leg's mark are still
                    // up, and a PointerDown that lands on one starts a resize instead of a draw.
                    select.Invoke(editor, [null]);
                    Pump();
                    foreach (var stale in canvas.Children.OfType<FrameworkElement>()
                                 .Where(c => c.Tag is AnnotationData).ToArray())
                        canvas.Children.Remove(stale);
                    toolField.SetValue(editor, EditorTool.Rectangle);
                    styleField.SetValue(editor, style);
                    bumpField.SetValue(editor, bump);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    Pump();

                    var origin = new Point(20, 20);
                    var draw = new double[60];
                    editor.PointerDown(Args(new Point(origin.X, origin.Y)));
                    Pump();
                    var watch = new Stopwatch();
                    for (int step = 0; step < draw.Length; step++)
                    {
                        double f = (step + 1) / (double)draw.Length;
                        var to = new Point(origin.X + box.Width * f, origin.Y + box.Height * f);
                        watch.Restart();
                        editor.PointerMove(Args(to));
                        canvas.UpdateLayout();
                        draw[step] = watch.Elapsed.TotalMilliseconds;
                        Pump();
                    }
                    var far = new Point(origin.X + box.Width, origin.Y + box.Height);
                    editor.PointerUp(Args(far));
                    Pump();

                    // The committed cloud is selected, so its handles are up: grab the
                    // bottom-right one and drag it, which is the resize leg.
                    var mark = canvas.Children.OfType<FrameworkElement>()
                        .LastOrDefault(c => c.Tag is AnnotationData);
                    if (mark is null) return (draw, []);
                    double left = Canvas.GetLeft(mark); if (double.IsNaN(left)) left = 0;
                    double top = Canvas.GetTop(mark); if (double.IsNaN(top)) top = 0;
                    // A cloud's grabbers ride the padded crown hull; a plain rectangle's ride
                    // its own box, which is 2 units of frame padding out.
                    Rect frame = mark is WpfPath cloud
                        ? CloudPath.SelectionFrame(cloud.Data, cloud.StrokeThickness)
                        : new Rect(0, 0, mark.Width, mark.Height);
                    var grab = new Point(frame.Right + left, frame.Bottom + top);

                    var resize = new double[60];
                    editor.PointerDown(Args(grab));
                    Pump();
                    for (int step = 0; step < resize.Length; step++)
                    {
                        // Wobble the corner rather than run away with it: every frame is a
                        // different box, so nothing can be cached by luck, but the cloud stays
                        // the size the row claims to be measuring.
                        double d = (step % 2 == 0 ? 1 : -1) * (8 + step % 7);
                        var to = new Point(grab.X + d, grab.Y + d);
                        watch.Restart();
                        editor.PointerMove(Args(to));
                        canvas.UpdateLayout();
                        resize[step] = watch.Elapsed.TotalMilliseconds;
                        Pump();
                    }
                    editor.PointerUp(Args(grab));
                    Pump();
                    return (draw, resize);
                }

                EditorWindow.PointerArgs Args(Point c) =>
                    new(c, canvas.TranslatePoint(c, viewport), MouseButton.Left, 1);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        if (!ran) return;

        Emit("cloud-perf-drag.txt", report.ToString());

        // Same tripwire logic as above: 60 ms/frame is one frame in four at 60 Hz, which is
        // "the editor is unusable", not "the box was busy".
        foreach ((string label, double p95) in results)
            Assert.True(p95 < 60, $"drag of {label} ran at {p95:F1} ms/frame p95\n{report}");
    }

    /// <summary>One Arrange plus one raster of the cloud on the slot it was built for.</summary>
    private static void RenderOnce(Geometry cloud, Rect box, double stroke)
    {
        var shape = new WpfPath
        {
            Data = cloud,
            Stroke = Brushes.Black,
            StrokeThickness = stroke,
            Fill = Brushes.Transparent,
            Width = box.Width,
            Height = box.Height,
        };
        var host = new Canvas { Width = box.Width, Height = box.Height };
        host.Children.Add(shape);
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        var rtb = new RenderTargetBitmap(
            (int)box.Width, (int)box.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
    }

    /// <summary>
    /// Median of <paramref name="runs"/> timings of <paramref name="work"/>, each on its own
    /// fresh input from <paramref name="setup"/> — which is NOT timed. Every cache WPF keeps on
    /// a Geometry instance is per-instance, so timing repeated work on one geometry measures
    /// the cache, not the work a drag actually does.
    /// </summary>
    private static double Median<T>(Func<T> setup, Action<T> work, int runs = 9)
    {
        for (int i = 0; i < 2; i++) work(setup());
        var times = new double[runs];
        var watch = new Stopwatch();
        for (int i = 0; i < runs; i++)
        {
            T input = setup();
            watch.Restart();
            work(input);
            times[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[runs / 2];
    }

    private static void Emit(string file, string report)
    {
        Console.WriteLine(report);
        string? dir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, file), report);
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
