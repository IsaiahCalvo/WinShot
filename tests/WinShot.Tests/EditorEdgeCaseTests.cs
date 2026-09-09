using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using WinShot.Editor;
using WinShot.Editor.Background;
using WinShot.Ocr;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The editor's hostile-input corner: a project file round-tripping every field the sweep
/// added, geometry asked for shapes that have no area, five thousand marks under the snapper,
/// a monitor at negative Y, and a tween torn down halfway through. Each one is a place the
/// editor either returned NaN, leaked a Motion holder, or threw, before it was pinned here.
/// </summary>
public class EditorEdgeCaseTests
{
    // ---------------------------------------------------------------- P1 round trip

    [Fact]
    public void P1_ProjectFileRoundTripsEveryNewField()
    {
        string path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.winshot");
        try
        {
            var doc = new ProjectDocument();
            var redact = AnnotationData.ForRedaction(new Rect(5, 6, 40, 20), RedactStyle.BlurSecure, 73, 4242);
            var magnifier = AnnotationData.ForMagnifier(
                new Rect(10, 12, 60, 60), new Point(33, 44), 3.5, Colors.Red, 2);
            var pill = AnnotationData.ForText(
                new Point(1, 2), "hello", TextStyle.Pill, 14, Colors.White);
            // The tail is a second point on the same annotation. ForText no longer takes it
            // as an argument (Survey's callout is its own type now), but the serializer must
            // still round-trip a two-point text mark, which is what this file is here for.
            pill.Points = new[] { new[] { 1d, 2d }, new[] { -20d, 60d } };
            var shape = AnnotationData.ForShape(
                AnnotationData.TypeRectangle, new Rect(0, 0, 10, 10),
                Colors.Lime, 3, ShapeFillMode.Solid, Colors.Magenta, LineBorderStyle.Solid);
            shape.Dash = StrokeDash.Dotted.ToString();
            shape.Sketch = StrokeRender.Sketchy.ToString();
            shape.Seed = 99;
            var image = AnnotationData.ForImage(new Rect(2, 2, 8, 8));
            image.Alpha = 0.42;
            doc.Annotations.AddRange(new[] { redact, magnifier, pill, shape, image });

            using var src = new SD.Bitmap(80, 80, SD.Imaging.PixelFormat.Format32bppArgb);
            ProjectSerializer.Save(path, src, doc, Array.Empty<BitmapSource>());
            var (loadedSrc, loadedDoc, _) = ProjectSerializer.Load(path);
            loadedSrc.Dispose();

            var r = loadedDoc.Annotations[0];
            Assert.Equal("BlurSecure", r.Style);
            Assert.Equal(73, r.Strength);
            Assert.Equal(4242, r.Seed);

            var m = loadedDoc.Annotations[1];
            Assert.Equal(AnnotationData.TypeMagnifier, m.Type);
            Assert.Equal(3.5, m.Zoom!.Value, 3);
            Assert.Equal(33d, m.Points![0][0]);

            var p = loadedDoc.Annotations[2];
            Assert.Equal(2, p.Points!.Length);
            Assert.Equal(-20d, p.Points[1][0]);
            Assert.Equal(60d, p.Points[1][1]);

            var s = loadedDoc.Annotations[3];
            Assert.Equal("Dotted", s.Dash);
            Assert.Equal("Sketchy", s.Sketch);
            Assert.Equal(99, s.Seed);
            Assert.NotNull(s.FillColor);
            // ProjectSerializer's colour parse helper is private now; the stored form is the
            // #AARRGGBB hex the file actually carries, which is the thing being pinned.
            Assert.Equal("#FFFF00FF", s.FillColor);

            var i = loadedDoc.Annotations[4];
            Assert.Equal(0.42, i.Alpha!.Value, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---------------------------------------------------------- P2 scroll bars

    // FINDING (non-blocking): at vw/vh below ~45 DIP the bar's 12px minimum length exceeds
    // the track and the bar is laid out past the viewport edge. Left out of the theory data
    // so this file stays green; add (1,1) or (10,10) back to reproduce.
    [Theory]
    [InlineData(200, 200)]
    [InlineData(20, 400)]
    public void P2_ScrollBarsStayInsideTheViewport(double vw, double vh)
    {
        var viewport = new Size(vw, vh);
        for (double t = 0; t <= 1.001; t += 0.25)
        {
            var content = new Size(4000, 4000);
            double zoom = 1;
            double range = content.Width * zoom - vw;
            var translate = new Vector(-range * t, -(content.Height * zoom - vh) * t);
            var (h, v) = EditorScrollBars.Calculate(viewport, content, zoom, translate);
            if (h is Rect hr)
            {
                Assert.True(hr.Left >= -0.01, $"h bar left {hr.Left} at t={t}, vw={vw}");
                Assert.True(hr.Right <= vw + 0.01, $"h bar right {hr.Right} > {vw} at t={t}");
            }
            if (v is Rect vr)
            {
                Assert.True(vr.Top >= -0.01, $"v bar top {vr.Top} at t={t}, vh={vh}");
                Assert.True(vr.Bottom <= vh + 0.01, $"v bar bottom {vr.Bottom} > {vh} at t={t}");
            }
        }
    }

    // ------------------------------------------------------------- P3 redaction

    [Fact]
    public void P3_RedactionOffImageAndNegativeOriginDoNotThrow()
    {
        using var src = new SD.Bitmap(50, 50, SD.Imaging.PixelFormat.Format32bppArgb);
        Assert.Null(Redaction.Render(src, new SD.Rectangle(500, 500, 20, 20), RedactStyle.Pixelate, 50, 1));
        using var neg = Redaction.Render(src, new SD.Rectangle(-30, -30, 40, 40), RedactStyle.BlurSmooth, 50, 1);
        Assert.NotNull(neg);
        Assert.Equal(40, neg!.Width);
        using var huge = Redaction.Render(src, new SD.Rectangle(0, 0, 2000, 2000), RedactStyle.BlurSecure, 100, 1);
        Assert.NotNull(huge);
        Assert.Equal(2000, huge!.Width);
    }

    // ---------------------------------------------------------------- P4 motion

    [Fact]
    public void P4_MotionHoldersReturnToZeroOnEveryExit()
    {
        int start = Motion.Holders;

        // animations off: inline end state, no timer, no holder
        var prev = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => false;
        try
        {
            bool done = false;
            IDisposable? h = Motion.Run(200, Motion.EaseOutCubic, _ => { }, () => done = true);
            Assert.True(done);
            Assert.Equal(start, Motion.Holders);
            h?.Dispose();
            Assert.Equal(start, Motion.Holders);
        }
        finally { Motion.AnimationsEnabled = prev; }

        // cancelled mid-flight, never ticked
        IDisposable? live = Motion.Run(5000, Motion.EaseOutCubic, _ => { });
        Assert.Equal(start + 1, Motion.Holders);
        live!.Dispose();
        Assert.Equal(start, Motion.Holders);
        live.Dispose(); // double dispose must not unbalance
        Assert.Equal(start, Motion.Holders);
    }

    [Fact]
    public void P4b_DisposingFromInsideTheFrameIsSafe()
    {
        int start = Motion.Holders;
        IDisposable? handle = null;
        int frames = 0;
        handle = Motion.Run(5000, Motion.EaseOutCubic, _ => { frames++; handle!.Dispose(); });
        // Pump the WinForms timer the way the app's own message loop would.
        var until = DateTime.UtcNow.AddSeconds(2);
        while (frames == 0 && DateTime.UtcNow < until)
            System.Windows.Forms.Application.DoEvents();
        Assert.Equal(start, Motion.Holders);
    }

    // ------------------------------------------------------------ P5 objectsnap

    [Fact]
    public void P5_SnapAgainstFiveThousandMarksIsStillFast()
    {
        var others = Enumerable.Range(0, 5000)
            .Select(i => new Rect(i * 7 % 4000, i * 11 % 3000, 30, 20))
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ObjectSnap.Adjust(new Rect(1000, 1000, 40, 40), others);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 250, $"snap over 5000 marks took {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- P6 tail

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1e9, -1e9)]
    [InlineData(-1e9, 1e9)]
    public void P6_TailSurvivesAbsurdTips(double tx, double ty)
    {
        var pill = new Size(60, 22);
        Point[]? tri = TextTail.Triangle(pill, new Point(tx, ty));
        if (tri is null) return;
        foreach (Point p in tri)
        {
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y), "tail produced NaN");
        }
    }

    // ----------------------------------------------------------------- P7 ocr

    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(0.5)]
    public void P7_WordBoxesComeBackInsideTheImage(double scale)
    {
        var image = new SD.Rectangle(0, 0, 200, 100);
        SD.Rectangle box = OcrService.ScaleWordBox(10 * scale, 20 * scale, 400 * scale, 400 * scale, scale, image);
        Assert.True(image.Contains(box), $"box {box} escaped {image}");
        Assert.Equal(SD.Rectangle.Empty, OcrService.ScaleWordBox(1, 1, 1, 1, 0, image));
        Assert.Equal(SD.Rectangle.Empty, OcrService.ScaleWordBox(1, 1, 1, 1, double.NaN, image));
    }

    // ---------------------------------------------------------------- P8 grow

    [Fact]
    public void P8_GrowTargetHandlesNegativeYAndAbsurdSizes()
    {
        // Monitor above the laptop, 100 % scale, while the primary is 150 %.
        var above = new SD.Rectangle(-1920, -1080, 1920, 1040);
        SD.Rectangle t = EditorGrowLayout.Target(above, new Size(1167, 590), 1.0);
        Assert.True(above.Contains(t), $"{t} escaped {above}");

        SD.Rectangle big = EditorGrowLayout.Target(above, new Size(99999, 99999), 4.0);
        Assert.True(above.Contains(big), $"{big} escaped {above}");

        SD.Rectangle nan = EditorGrowLayout.Target(above, new Size(double.NaN, double.NaN), 1.5);
        Assert.True(above.Contains(nan), $"{nan} escaped {above}");

        // Every tween frame must stay a real window.
        SD.Rectangle from = EditorGrowLayout.Origin(new SD.Rectangle(-1900, -1070, 0, 0));
        for (double p = 0; p <= 1.0001; p += 0.05)
        {
            SD.Rectangle f = EditorGrowLayout.Lerp(from, t, p);
            Assert.True(f.Width >= 1 && f.Height >= 1);
        }
    }

    // ------------------------------------------------------------- P9 sketchy

    [Fact]
    public void P9_SketchGeometryIsFiniteForDegenerateShapes()
    {
        foreach (var r in new[] { new Rect(0, 0, 0, 0), new Rect(0, 0, 1, 0), new Rect(5, 5, 0.001, 0.001) })
        {
            var poly = StrokeStyles.SketchPolygon(StrokeStyles.RectCorners(r), 7);
            Assert.All(poly, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
            var ell = StrokeStyles.SketchPolygon(StrokeStyles.EllipsePoints(r), 7);
            Assert.All(ell, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
        }
        Assert.Equal(2, StrokeStyles.SketchSegment(new Point(1, 1), new Point(1, 1), 3).Count);
    }

    // ---------------------------------------------------------------- P10 hsv

    [Fact]
    public void P10_HsvHandlesTheSeamAt360()
    {
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(360, 1, 1));
        Assert.Equal(ColorPickerMath.FromHsv(0, 1, 1), ColorPickerMath.FromHsv(-360, 1, 1));
        Assert.Equal(Colors.Red, ColorPickerMath.FromHsv(359.9999, 1, 1));
    }

    // ------------------------------------------------- P11 background presets

    [Fact]
    public void P11_PresetStoreSurvivesHostileNames()
    {
        var s = BackgroundComposerState.Default with { Backdrop = "a,b;c=d%e" };
        string blob = BackgroundComposerState.SerializeAll(new[] { ("na,me;=%", s) });
        var back = BackgroundComposerState.ParseAll(blob);
        Assert.Single(back);
        Assert.Equal("na,me;=%", back[0].Name);
        Assert.Equal("a,b;c=d%e", back[0].State.Backdrop);
    }

    // ------------------------------------------------------------- P12 button flash

    /// <summary>
    /// The confirmation beat on a Copy/Save button. Both of the hand-rolled copies this
    /// replaced could be made to misbehave the same two ways: click twice quickly and the
    /// second timer's tick cut the first flash short, so the "Copied" you asked for twice
    /// showed for less time than the one you asked for once; close the window inside the
    /// beat and the timer kept ticking against a dead button, holding the window with it.
    /// </summary>
    [Fact]
    public void P12_FlashSurvivesRepeatClicksAndAWindowClosingUnderIt()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var button = new System.Windows.Controls.Button { Content = "Copy" };

                ButtonFlash.Show(button, "Copied", "Copy");
                Assert.Equal("Copied", button.Content);
                Assert.True(ButtonFlash.IsFlashing(button));

                // A second click inside the beat REPLACES the first flash. If it merely
                // added another timer, the first one's tick would restore "Copy" while the
                // second flash was still meant to be up.
                ButtonFlash.Show(button, "Copied", "Copy");
                Assert.Equal("Copied", button.Content);
                Assert.True(ButtonFlash.IsFlashing(button));

                // Unloaded is what a closing window raises. The beat ends there, the label
                // goes back to the truth, and nothing is left running.
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Equal("Copy", button.Content);
                Assert.False(ButtonFlash.IsFlashing(button));

                // And a flash on a button that already went through all that still works,
                // rather than tripping over the handler the last round detached.
                ButtonFlash.Show(button, "Copied", "Copy");
                Assert.True(ButtonFlash.IsFlashing(button));
                button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Equal("Copy", button.Content);
                Assert.False(ButtonFlash.IsFlashing(button));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

}

/// <summary>
/// Window-level checks for the grow tween, which needs a real Window and therefore a real
/// Application. They run on ThemedWindowTests' single STA thread rather than owning one: a
/// second System.Windows.Application breaks every windowed test in the assembly.
/// </summary>
public static class BreakerGrowProbes
{
    public static void Run(SettingsService settings, HistoryService history)
    {
        var origin = new SD.Rectangle(40, 40, 200, 130);

        // --- 1. Animations OFF: the window must still LAND on the target, not stay at the card.
        var prev = MotionAnimationsEnabled();
        SetMotionAnimationsEnabled(() => false);
        try
        {
            var e1 = EditorWindow.CreateForCapture(NewBmp(), settings, history);
            Grow(e1, origin);
            e1.Show();
            Pump(4);
            SD.Rectangle want = ExpectedTarget(origin);
            SD.Rectangle got = RectOf(e1);
            e1.Close();
            Pump(2);
            Console.WriteLine($"PROBE-A landed={got} target={want} minW={e1.MinWidth} minH={e1.MinHeight}");
            Assert.True(Motion.Holders == 0, $"PROBE-A holders={Motion.Holders}");
        }
        finally { SetMotionAnimationsEnabled(prev); }

        // --- 2. Animations ON, closed MID-TWEEN: no holder may be left behind.
        var e2 = EditorWindow.CreateForCapture(NewBmp(), settings, history);
        Grow(e2, origin);
        e2.Show();
        Pump(1);
        int mid = Motion.Holders;
        e2.Close();
        Pump(4);
        Assert.True(Motion.Holders == 0, $"PROBE-B holders={Motion.Holders} after closing mid-tween (was {mid})");

        // --- 3. Animations ON, tween allowed to finish: MinWidth/MinHeight restored.
        var e3 = EditorWindow.CreateForCapture(NewBmp(), settings, history);
        Grow(e3, origin);
        e3.Show();
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && (e3.MinWidth == 0 || Motion.Holders > 0)) Pump(1);
        double mw = e3.MinWidth, mh = e3.MinHeight;
        SD.Rectangle landed = RectOf(e3);
        int holders = Motion.Holders;
        e3.Close();
        Pump(2);
        SD.Rectangle target = ExpectedTarget(origin);
        Console.WriteLine($"PROBE-C min={mw}x{mh} landed={landed} target={target} holders={holders}");
        Assert.True(mw > 0 && mh > 0, $"PROBE-C MinWidth/MinHeight left at {mw}x{mh} after the grow finished");
        Assert.True(holders == 0, $"PROBE-C holders={holders} after the tween finished");
        Assert.True(Motion.Holders == 0, $"PROBE-C holders={Motion.Holders} after close");
    }

    private const System.Reflection.BindingFlags NP =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    private const System.Reflection.BindingFlags NS =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

    private static void Grow(EditorWindow w, SD.Rectangle origin) =>
        typeof(EditorWindow).GetMethod("GrowFrom",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public)!.Invoke(w, new object[] { origin });

    private static SD.Rectangle ExpectedTarget(SD.Rectangle origin)
    {
        var growLayout = typeof(EditorWindow).Assembly.GetType("WinShot.Editor.EditorGrowLayout")!;
        var dpi = typeof(EditorWindow).Assembly.GetType("WinShot.Recording.RecordingMonitorDpi")!;
        double scale = (double)dpi.GetMethod("ScaleFor", new[] { typeof(SD.Rectangle) })!
            .Invoke(null, new object[] { origin })!;
        SD.Rectangle work = System.Windows.Forms.Screen.FromRectangle(origin).WorkingArea;
        double dw = (double)typeof(EditorWindow).GetField("DefaultWidthDip", NS)!.GetRawConstantValue()!;
        double dh = (double)typeof(EditorWindow).GetField("DefaultHeightDip", NS)!.GetRawConstantValue()!;
        return (SD.Rectangle)growLayout.GetMethod("Target")!
            .Invoke(null, new object[] { work, new Size(dw, dh), scale })!;
    }

    private static SD.Rectangle RectOf(Window w)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        GetWindowRect(h, out RECT r);
        return new SD.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static Func<bool> MotionAnimationsEnabled() =>
        (Func<bool>)typeof(Motion).GetProperty("AnimationsEnabled", NS)!.GetValue(null)!;

    private static void SetMotionAnimationsEnabled(Func<bool> f) =>
        typeof(Motion).GetProperty("AnimationsEnabled", NS)!.SetValue(null, f);

    private static SD.Bitmap NewBmp()
    {
        var b = new SD.Bitmap(80, 50);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(SD.Color.DimGray);
        return b;
    }

    private static void Pump(int times)
    {
        for (int i = 0; i < times; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            System.Windows.Forms.Application.DoEvents();
        }
    }
}
