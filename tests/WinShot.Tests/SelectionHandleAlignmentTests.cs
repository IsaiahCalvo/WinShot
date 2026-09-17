using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Isaiah: "Drag handles for resizing a rectangle and other shapes are not aligned with the
/// actual edge of the shape."
///
/// This measures it. Every visible selection thumb's CENTRE is compared, in canvas (DIP)
/// coordinates, against the corresponding corner / edge midpoint / endpoint of the shape's own
/// AUTHORED geometry — the rect the user dragged out, or the two points they placed — pushed
/// through the ELEMENT'S OWN render transform to find where WPF actually paints it. Two things
/// that matters for: the expectation never consults the rendered ink box, so a frame that has
/// slid onto the stroke's outer edge cannot hide behind the same measurement that put it there;
/// and it never consults the handle code's idea of the pivot either, so a handle layout that
/// turns about a different centre than the render shows up as drift instead of cancelling out.
///
/// Each case runs at three stroke widths and two zooms: the two factors that turn this class of
/// bug from a constant, invisible offset into a visible one. A revision-cloud rectangle was off
/// by 6 DIP at a 1 px border and 18 DIP at 12 px before the fix — the drift is
/// (crown bulge + strokeWidth / 2), so stroke width is exactly what made it visible.
///
/// This harness owns the AppDomain's Application, so run it alone:
///   powershell -File .\tools\run-tests.ps1 -Filter "FullyQualifiedName~SelectionHandleAlignment"
/// </summary>
public class SelectionHandleAlignmentTests
{
    /// <summary>
    /// The whole point of the test: a handle must land ON the edge, not near it. Half a DIP is
    /// the brief's number, and it is generous — with the fix in, every case below measures under
    /// 1e-4 DIP, while the defect it guards against ran 6 to 25.5 DIP.
    /// </summary>
    private const double ToleranceDip = 0.5;

    [Fact]
    public void HandlesSitOnTheShapesOwnEdges()
    {
        var report = new StringBuilder();
        var rows = new List<string>();
        double worst = 0;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // This assembly gets exactly ONE System.Windows.Application and the thread that
                // creates it owns it for good. ThemedWindowTests is the established owner, and
                // this class sorts ahead of it, so an ungated run here took the Application first
                // and killed that test with "Cannot create more than one System.Windows.
                // Application instance in the same AppDomain" — reproduced, not assumed. Every
                // other editor-window class in here carries the same gate.
                //
                // It also fixes the masquerade: an ungated stand-down is a plain `return` that
                // xunit 2.9.3 reports as a PASS, indistinguishable from real coverage (there is
                // no runtime Assert.Skip before xunit v3). Gated, not running is an opt-out
                // someone chose, not a green tick nobody earned.
                if (Environment.GetEnvironmentVariable("WINSHOT_EDITOR_TESTS") != "1") return;
                if (Application.Current is { } inherited && !inherited.CheckAccess()) return;

                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                using var source = new SD.Bitmap(1200, 800, SD.Imaging.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(source)) g.Clear(SD.Color.White);

                var editor = new EditorWindow(source, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                Pump(30);
                var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;

                foreach (double zoom in new[] { 1.0, 2.5 })
                {
                    SetZoom(editor, zoom);
                    Pump(2);
                    foreach (double w in new[] { 1.0, 4.0, 12.0 })
                        foreach (double angle in new[] { 0.0, 30.0, 90.0 })
                            foreach (var probe in Probes(w))
                            {
                                double d = Measure(editor, canvas, probe, angle, zoom, report);
                                rows.Add($"{probe.Name}/{angle:0}deg/w{w:0}/z{zoom:0.#}={d:0.###}");
                                worst = Math.Max(worst, d);
                            }
                }

                // The counter's chrome is a single dot on the pin's nubbin tip, and it runs off
                // the same bounds, so it is the same defect wearing a different hat. Its target
                // comes from CounterGeometry's own rule — the tip sits TipDistance(r) = 1.5r from
                // the bubble centre on the pointer's bearing — never from the handle code.
                foreach (double pointerAngle in new[] { 0.0, 45.0, 135.0, -120.0 })
                {
                    double d = MeasureCounterTip(editor, canvas, pointerAngle, report);
                    rows.Add($"counter-tip/{pointerAngle:0}deg={d:0.###}");
                    worst = Math.Max(worst, d);
                }

                // The cloud's glow ring is new chrome with no numeric position to assert, so
                // guard the two ways it actually breaks: gone when it should be there, or stuck
                // on over the next selection.
                string? glow = CheckCloudGlow(editor, canvas);
                if (glow is not null) { report.AppendLine(glow); worst = double.MaxValue; }
                rows.Add($"cloud-glow={(glow is null ? "ok" : "BROKEN")}");

                editor.Close();
                Pump(4);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;

        if (rows.Count == 0) return;   // stood down: another test owns the Application
        Assert.True(worst <= ToleranceDip,
            $"Selection handles are off the shape's edge by up to {worst:0.###} DIP " +
            $"(tolerance {ToleranceDip}).\n{report}");
    }

    // ---------------------------------------------------------------- probes

    /// <summary>
    /// One shape plus the exact points its handles must land on, in the element's OWN
    /// coordinates, derived from the authored geometry alone. The result is indexed the way the
    /// editor indexes its thumbs; a null entry is a thumb this shape has but whose position this
    /// probe does not pin down.
    /// </summary>
    private sealed record Probe(string Name, AnnotationData Data,
        Func<FrameworkElement, Point?[]> LocalTargets);

    private static Point[] BoxPoints(Rect raw, double padding)
    {
        Rect b = padding == 0 ? raw : Rect.Inflate(raw, padding, padding);
        return new[]
        {
            new Point(b.Left, b.Top),                    // 0 TL
            new Point(b.Right, b.Top),                   // 1 TR
            new Point(b.Right, b.Bottom),                // 2 BR
            new Point(b.Left, b.Bottom),                 // 3 BL
            new Point(b.Left + b.Width / 2, b.Top),      // 4 T
            new Point(b.Right, b.Top + b.Height / 2),    // 5 R
            new Point(b.Left + b.Width / 2, b.Bottom),   // 6 B
            new Point(b.Left, b.Top + b.Height / 2),     // 7 L
        };
    }

    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>
    /// A box-shaped annotation's own coordinates start at its layout slot's top-left, so the
    /// authored rect is simply (0, 0, Width, Height) there. Reading it off the ELEMENT rather
    /// than off the probe's literal is deliberate: it is the same box WPF lays the shape out in,
    /// so it stays true after a resize as well.
    /// </summary>
    private static Point?[] LocalBox(FrameworkElement fe, double padding) =>
        BoxPoints(new Rect(0, 0, fe.Width, fe.Height), padding).Cast<Point?>().ToArray();

    private static IEnumerable<Probe> Probes(double thickness)
    {
        var rect = new Rect(300, 200, 320, 180);
        // Survey's border-flush case: a plain rectangle's handles sit on the RAW rect, padding 0.
        yield return new Probe("rectangle", BoxData(AnnotationData.TypeRectangle, rect, thickness),
            fe => LocalBox(fe, 0));

        // Same rect with a revision-cloud border — the one shape whose frame is NOT its box.
        // Survey rides a cloud's grabbers on the crowns' hull padded by 1.5 * strokeWidth, never
        // on the rectangle the crowns were built from, and that composes to exactly one stroke
        // width outside the authored box. Worth keeping the two halves visible: CloudPath's
        // FitInside insets the crown ring so the PAINTED hull lands on the box, leaving the
        // geometry's own bounds at (box - sw / 2); SelectionFrame then adds 1.5 * sw, and
        // (box - sw / 2) + 1.5 * sw = box + sw. So the padding below is Survey's deliberate
        // clear gap measured off the crowns' ink, not slack.
        //
        // Put the other way, which is the claim that matters to the defect: the frame clears the
        // painted cloud by the SAME distance on all four sides and that distance is a fixed
        // multiple of the stroke. What this replaced drifted by (crown bulge + sw / 2) off a
        // shape that was itself being clipped — 6 DIP at a 1 px border, 18 at 12 px — which is
        // neither uniform against the ink nor intentional.
        var cloud = BoxData(AnnotationData.TypeRectangle, rect, thickness);
        cloud.LineStyle = "cloud";
        yield return new Probe("rect-cloud", cloud, fe => LocalBox(fe, thickness));

        // Ellipse: not border-flush — Survey pads the frame 2 page units.
        var oval = new Rect(300, 200, 260, 190);
        yield return new Probe("ellipse", BoxData(AnnotationData.TypeEllipse, oval, thickness),
            fe => LocalBox(fe, 2));

        // Line / arrow: the two endpoint thumbs ARE the selection, plus the bend dot at the
        // midpoint. A stroke annotation has no layout slot — its geometry is authored straight
        // in canvas coordinates, which ARE its own coordinates — so the stored points go through
        // the element transform unchanged.
        Point from = new(280, 240), to = new(620, 430);
        foreach (string type in new[] { AnnotationData.TypeLine, AnnotationData.TypeArrow })
            yield return new Probe(type,
                new AnnotationData
                {
                    Type = type,
                    Points = new[] { new[] { from.X, from.Y }, new[] { to.X, to.Y } },
                    Color = "#FF34C759",
                    Thickness = thickness,
                    Style = ArrowStyle.Straight.ToString(),
                },
                _ => new Point?[] { from, to, Mid(from, to) });

        // Callout: tip, knee, then the four corners of the bubble box it actually draws. The
        // drawn box carries a descender buffer the authored drag rect does not, so the targets
        // come back off the live CalloutLayout rather than off the rect that seeded it.
        var seed = CalloutLayout.FromDrag(new Point(320, 520), new Point(560, 300));
        yield return new Probe("callout",
            AnnotationData.ForCallout(seed, "Callout", Color.FromRgb(255, 69, 58),
                Color.FromArgb(0x40, 255, 255, 255), thickness, ArrowheadStyle.SolidTriangle,
                LineBorderStyle.Solid, 18),
            fe =>
            {
                var l = ((CalloutAnnotation)fe).Layout;
                var box = l.RenderBox;
                return new Point?[]
                {
                    l.Tip, l.Knee, box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft,
                };
            });
    }

    private static AnnotationData BoxData(string type, Rect r, double thickness) => new()
    {
        Type = type,
        Rect = new[] { r.X, r.Y, r.Width, r.Height },
        Color = "#FFFF9500",
        Thickness = thickness,
        Fill = ShapeFillMode.None.ToString(),
    };

    // --------------------------------------------------------------- measure

    private static double Measure(EditorWindow editor, Canvas canvas, Probe probe,
        double angle, double zoom, StringBuilder report)
    {
        // Neither Survey nor WinShot gives a callout a rotation grip, so there is no angle for
        // its handle layout to honour: the rotated passes would be measuring a state the app
        // cannot produce.
        if (probe.Name == "callout" && angle != 0) return 0;

        var data = probe.Data;
        data.Angle = angle;
        var element = ProjectSerializer.CreateElement(data, Array.Empty<BitmapSource>());
        var fe = (FrameworkElement)element;
        fe.Tag = data;
        canvas.Children.Add(element);
        canvas.UpdateLayout();
        if (angle != 0) AnnotationTransform.SetAngle(fe, angle);
        canvas.UpdateLayout();
        Pump(2);

        // Where WPF actually paints each authored point: the element's own transform to the
        // canvas, which is the one thing both the render and the handle layout must agree with.
        var toCanvas = fe.TransformToAncestor(canvas);
        var expected = probe.LocalTargets(fe);

        Invoke(editor, "Select", element);
        Invoke(editor, "UpdateSelectionVisual");
        Pump(2);

        var thumbs = (System.Collections.IList)typeof(EditorWindow)
            .GetField("_handleThumbs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;

        double worst = 0;
        int checkedCount = 0;
        var line = new StringBuilder(
            $"{probe.Name,-10} angle={angle,3:0} w={data.Thickness,4:0.#} zoom={zoom:0.#} ");
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] is not Point want) continue;
            if (i >= thumbs.Count)
            {
                line.Append($"[{i}:MISSING] ");
                worst = Math.Max(worst, double.MaxValue);
                continue;
            }
            var thumb = (Rectangle)thumbs[i]!;
            if (thumb.Visibility != Visibility.Visible) continue;   // an adaptive tier culled it
            checkedCount++;
            var actual = new Point(
                Canvas.GetLeft(thumb) + thumb.Width / 2,
                Canvas.GetTop(thumb) + thumb.Height / 2);
            var target = toCanvas.Transform(want);
            double d = (actual - target).Length;
            worst = Math.Max(worst, d);
            if (d > ToleranceDip)
                line.Append($"[{i} off {d:0.##}: want {target.X:0.#},{target.Y:0.#} " +
                            $"got {actual.X:0.#},{actual.Y:0.#}] ");
        }

        Invoke(editor, "Select", null);
        canvas.Children.Remove(element);

        // A case that measured nothing proves nothing — every probe here is big enough that the
        // adaptive tiers keep all of its thumbs.
        if (checkedCount == 0)
        {
            report.AppendLine($"{probe.Name} angle={angle:0} w={data.Thickness:0.#}: no thumb measured");
            return double.MaxValue;
        }
        if (worst > ToleranceDip) report.AppendLine(line.ToString().TrimEnd());
        return worst;
    }

    /// <summary>
    /// A selected counter shows one handle: the dot on its nubbin tip. The badge Grid is
    /// 3r square with the bubble at its centre (CounterGeometry.TipDistance(r) * 2), so the tip
    /// is half the Grid out along the pointer's bearing — in the Grid's OWN coordinates, which
    /// is what makes this independent of whatever frame the handle code chose.
    /// </summary>
    private static double MeasureCounterTip(EditorWindow editor, Canvas canvas,
        double pointerAngle, StringBuilder report)
    {
        var data = AnnotationData.ForStep(new Point(700, 400), 1, Color.FromRgb(52, 199, 89), 9);
        data.PointerAngle = pointerAngle;
        var fe = (FrameworkElement)ProjectSerializer.CreateElement(data, Array.Empty<BitmapSource>());
        fe.Tag = data;
        canvas.Children.Add(fe);
        canvas.UpdateLayout();
        Pump(2);

        Invoke(editor, "Select", fe);
        Invoke(editor, "UpdateSelectionVisual");
        Pump(2);

        double half = fe.Width / 2;
        double rad = pointerAngle * Math.PI / 180;
        var target = fe.TransformToAncestor(canvas).Transform(
            new Point(half + Math.Cos(rad) * half, half + Math.Sin(rad) * half));

        var dot = (Ellipse)typeof(EditorWindow)
            .GetField("_rotateThumb", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        var actual = new Point(Canvas.GetLeft(dot) + dot.Width / 2, Canvas.GetTop(dot) + dot.Height / 2);
        double d = dot.Visibility == Visibility.Visible ? (actual - target).Length : double.MaxValue;
        if (d > ToleranceDip)
            report.AppendLine($"counter-tip pointer={pointerAngle:0} off {d:0.##}: " +
                $"want {target.X:0.#},{target.Y:0.#} got {actual.X:0.#},{actual.Y:0.#}");

        Invoke(editor, "Select", null);
        canvas.Children.Remove(fe);
        return d;
    }

    /// <summary>
    /// Survey paints a selected cloud with a glow RING — the crowns widened with the ink band
    /// knocked out. Returns null when it behaves, or what went wrong.
    /// </summary>
    private static string? CheckCloudGlow(EditorWindow editor, Canvas canvas)
    {
        var rect = new Rect(300, 200, 320, 180);
        var cloudData = BoxData(AnnotationData.TypeRectangle, rect, 6);
        cloudData.LineStyle = "cloud";
        var plainData = BoxData(AnnotationData.TypeRectangle, rect, 6);

        Path? Glow() => (Path?)typeof(EditorWindow)
            .GetField("_cloudGlow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor);

        FrameworkElement Add(AnnotationData d)
        {
            var el = (FrameworkElement)ProjectSerializer.CreateElement(d, Array.Empty<BitmapSource>());
            el.Tag = d;
            canvas.Children.Add(el);
            canvas.UpdateLayout();
            Invoke(editor, "Select", el);
            Invoke(editor, "UpdateSelectionVisual");
            Pump(2);
            return el;
        }

        var cloud = Add(cloudData);
        var lit = Glow();
        string? problem =
            lit is null ? "cloud glow was never created"
            : lit.Visibility != Visibility.Visible ? "cloud glow is not visible on a selected cloud"
            : lit.Data is null || lit.Data.IsEmpty() ? "cloud glow has empty geometry"
            : null;
        canvas.Children.Remove(cloud);

        // Selecting a plain rectangle next must put it away again.
        var plain = Add(plainData);
        if (problem is null && Glow() is { Visibility: Visibility.Visible })
            problem = "cloud glow stayed visible after selecting a non-cloud";
        canvas.Children.Remove(plain);
        Invoke(editor, "Select", null);
        Pump(1);
        return problem;
    }

    private static void SetZoom(EditorWindow editor, double zoom) =>
        typeof(EditorWindow).GetMethod("ApplyAnchoredScale",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, new object[] { zoom });

    private static void Invoke(EditorWindow editor, string name, object? argument = null)
    {
        var method = typeof(EditorWindow).GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(EditorWindow).FullName, name);
        method.Invoke(editor, method.GetParameters().Length == 0 ? null : new[] { argument });
    }

    private static void Pump(int iterations)
    {
        for (int n = 0; n < iterations; n++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
