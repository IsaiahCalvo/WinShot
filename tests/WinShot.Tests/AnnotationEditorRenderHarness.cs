using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows.Input;
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
/// Produces sanitized, in-process editor baseline renders without launching WinShot.exe.
/// Gated so normal test runs remain side-effect free.
/// </summary>
public class AnnotationEditorRenderHarness
{
    [Fact]
    public void RenderSanitizedAnnotationEditorBaseline()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_ANNOTATION") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-annotation-render");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var source = CreateSanitizedSource();
                var editor = new EditorWindow(source, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                Pump(45);

                var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
                SelectTool(editor, EditorTool.Select);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-primary-shell.png"));

                ((RadioButton)editor.FindName("FreehandToolBtn")!).IsChecked = true;
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-draw-group.png"));

                ((RadioButton)editor.FindName("RectangleToolBtn")!).IsChecked = true;
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-shape-group.png"));

                ((RadioButton)editor.FindName("TextToolBtn")!).IsChecked = true;
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-text-group.png"));

                var colorPicker = (System.Windows.Controls.Primitives.Popup)editor.FindName("ColorPickerPopup")!;
                typeof(EditorWindow).GetMethod("BuildColorPickerPalette",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                colorPicker.IsOpen = true;
                Pump(4);
                RenderElementToPng(colorPicker.Child, Path.Combine(outDir, "editor-after-color-picker.png"));
                colorPicker.IsOpen = false;

                var topToolbar = (Border)editor.FindName("TopToolbar")!;
                Pump(4);
                RenderElementToPng(topToolbar, Path.Combine(outDir, "editor-after-more-tools-overflow.png"));

                UIElement selectedRectangle = AddRepresentativeAnnotations(canvas);
                Pump(6);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-annotations-overview.png"));
                RenderToPng(editor, Path.Combine(outDir, "editor-after-shell-200pct.png"), dpi: 192);

                SelectTool(editor, EditorTool.Rectangle);
                InvokePrivate(editor, "Select", selectedRectangle);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-rectangle-selection-context.png"));

                // Survey's rotation pill beside the grip of a rotated shape.
                AnnotationTransform.SetAngle((FrameworkElement)selectedRectangle, 30);
                ((AnnotationData)((FrameworkElement)selectedRectangle).Tag!).Angle = 30;
                typeof(EditorWindow).GetMethod("UpdateSelectionVisual", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                Pump(2);
                typeof(EditorWindow).GetMethod("PlaceRotationPill", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, null);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-rotation-pill.png"));
                RenderToPng(editor, Path.Combine(outDir, "editor-after-rotation-pill-200pct.png"), dpi: 192);

                // Callout selection: knee/tip squares + corner circles, no rotation grip.
                var callout = canvas.Children.OfType<FrameworkElement>()
                    .First(f => f.Tag is AnnotationData { Type: AnnotationData.TypeCallout });
                InvokePrivate(editor, "Select", callout);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-callout-selection.png"));

                InvokePrivate(editor, "Select", null);
                SelectTool(editor, EditorTool.Blur);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-blur-strength-context.png"));

                SelectTool(editor, EditorTool.Text);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-text-style-context.png"));
                RenderToPng(editor, Path.Combine(outDir, "editor-after-text-style-context-200pct.png"), dpi: 192);

                // A live click-placed editor: descenders must not clip at the bottom.
                InvokePrivate(editor, "PlaceText", new Point(300, 330));
                Pump(2);
                var liveEditor = canvas.Children.OfType<TextBox>().Last();
                liveEditor.Text = "Typing gjpqy";
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-text-editing-200pct.png"), dpi: 192);
                liveEditor.Text = "";

                SelectTool(editor, EditorTool.Crop);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-crop-ratio-context.png"));

                canvas.Children.Clear();
                var spotlight = AnnotationFactory.CreateSpotlight(
                    new Size(source.Width, source.Height), new Rect(395, 155, 330, 245));
                spotlight.Tag = AnnotationData.ForSpotlight(
                    new Size(source.Width, source.Height), new Rect(395, 155, 330, 245));
                canvas.Children.Add(spotlight);
                var spotlightLabel = AnnotationFactory.CreateStyledTextLabel(
                    "Spotlight focus", Brushes.White, 32, TextStyle.Pill);
                Canvas.SetLeft(spotlightLabel, 445);
                Canvas.SetTop(spotlightLabel, 250);
                canvas.Children.Add(spotlightLabel);
                SelectTool(editor, EditorTool.Spotlight);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-spotlight-context.png"));

                File.WriteAllText(Path.Combine(outDir, "render-manifest.txt"),
                    "Sanitized WinShot annotation editor after-state\n" +
                    "Generated in-process with RenderTargetBitmap; WinShot.exe was not launched.\n" +
                    "Source content is synthetic and contains no user data.\n" +
                    "Renders: primary shell, Draw/Shape/Text groups, color picker, filled rectangle,\n" +
                    "More overflow, annotations (incl. dashed line, V-arrow, callout), 200% DPI,\n" +
                    "rectangle selection, blur strength, text style, crop ratio, and spotlight.\n");

                editor.Close();
                Pump(2);
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
        Assert.True(File.Exists(Path.Combine(outDir, "editor-after-primary-shell.png")),
            "No annotation editor overview PNG was produced.");
    }

    /// <summary>
    /// AUDIT-ONLY probe (gated on WINSHOT_RENDER_INKAUDIT=1): measures pen live-preview vs
    /// committed geometry and eraser determinism across widths and pointer speeds.
    /// </summary>
    [Fact]
    public void RenderInkAndEraserAudit()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_INKAUDIT") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-annotation-render");
        Directory.CreateDirectory(outDir);
        var log = new System.Text.StringBuilder();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var source = CreateSanitizedSource();
                var editor = new EditorWindow(source, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                Pump(45);

                var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;
                double W = source.Width, H = source.Height;
                var penSizeField = typeof(EditorWindow).GetField("_penSize",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var eraserSizeField = typeof(EditorWindow).GetField("_eraserSize",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

                EditorWindow.PointerArgs Pt(Point c) =>
                    new(c, canvas.TranslatePoint(c, viewport), MouseButton.Left, 1);
                void Check(string name)
                { ((RadioButton)editor.FindName(name)!).IsChecked = true; Pump(3); }

                double GeomArea(Geometry g) =>
                    g is null ? 0 : g.GetFlattenedPathGeometry().GetArea();
                int Figures(Geometry g) =>
                    g is null ? 0 : g.GetFlattenedPathGeometry().Figures.Count;

                // ---------- 1. pen live preview vs commit, several widths ----------
                // "slow" = hand samples ~0.25 canvas px apart (what a 120Hz pointer produces
                // at high zoom); "fast" = ~3.3 px apart. Only the slow case can expose the
                // 0.2 live dedupe vs 0.35 commit compaction as a shape change.
                foreach (var (pw, slow) in new[]
                {
                    (1.0, false), (2.0, false), (3.0, false), (12.0, false), (30.0, false),
                    (2.0, true), (3.0, true), (12.0, true),
                })
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, pw);
                    Check("FreehandToolBtn");

                    var path = new List<Point>();
                    int n = slow ? 900 : 240;
                    double span = slow ? 0.20 : 0.70;
                    for (int i = 0; i <= n; i++)
                    {
                        double t = i / (double)n;
                        path.Add(new Point(0.08 * W + span * W * t,
                                           0.40 * H + (slow ? 0.03 : 0.12) * H * Math.Sin(t * Math.PI * 3)));
                    }
                    editor.PointerDown(Pt(path[0])); Pump(1);
                    for (int i = 1; i < path.Count; i++) { editor.PointerMove(Pt(path[i])); Pump(1); }
                    Pump(3);
                    var live = canvas.Children.OfType<System.Windows.Shapes.Polyline>().Last();
                    int livePts = live.Points.Count;
                    string tag = $"w{pw}{(slow ? "-slow" : "")}";
                    RenderToPng(editor, Path.Combine(outDir, $"audit-pen-preview-{tag}.png"), dpi: 192);
                    // The reported symptom is that the stroke "resettles" on release, so diff the
                    // CANVAS across pointer-up. Canvas only, never the whole window: committing
                    // pushes an undo step, so a full-window diff also catches the undo arrow
                    // going from disabled-grey to enabled-white and reports ~190 changed pixels
                    // at maxChannelDelta 126 that contain no ink at all.
                    var canvasHost = (FrameworkElement)editor.FindName("CanvasHost")!;
                    using var beforeUp = BitmapEffects.RenderVisual(canvasHost, source.Width, source.Height);
                    editor.PointerUp(Pt(path[^1])); Pump(5);
                    RenderToPng(editor, Path.Combine(outDir, $"audit-pen-commit-{tag}.png"), dpi: 192);
                    using var afterUp = BitmapEffects.RenderVisual(canvasHost, source.Width, source.Height);
                    int popPixels = 0, popWorst = 0;
                    for (int y = 0; y < afterUp.Height; y++)
                        for (int x = 0; x < afterUp.Width; x++)
                        {
                            var p = beforeUp.GetPixel(x, y);
                            var q = afterUp.GetPixel(x, y);
                            int e = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                            if (e > 3) { popPixels++; if (e > popWorst) popWorst = e; }
                        }
                    var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    var compacted = PaperInk.Compact(live.Points);
                    log.AppendLine($"PEN {tag} rawSamples={path.Count} livePts(0.2)={livePts} " +
                        $"committedPts(0.35)={compacted.Count} " +
                        $"outlineFigures={Figures(ink.Data!)} " +
                        $"outlineVerts={FlatVertexCount(ink.Data!)} " +
                        $"area={GeomArea(ink.Data!):F2} " +
                        $"fillRule={((PathGeometry)ink.Data!).FillRule} " +
                        $"releasePopPixels={popPixels} releasePopWorst={popWorst}");
                }

                // ---------- 2. eraser determinism: widths x pointer speeds ----------
                foreach (double pw in new[] { 2.0, 12.0 })
                {
                    foreach (double stepPx in new[] { 0.5, 60.0 })
                    {
                        canvas.Children.Clear(); Pump(3);
                        penSizeField.SetValue(editor, pw);
                        Check("FreehandToolBtn");
                        // One long horizontal stroke.
                        var a = new Point(0.08 * W, 0.5 * H);
                        var b = new Point(0.92 * W, 0.5 * H);
                        editor.PointerDown(Pt(a)); Pump(1);
                        for (int i = 1; i <= 120; i++)
                            { editor.PointerMove(Pt(new Point(a.X + (b.X - a.X) * i / 120.0, a.Y))); Pump(1); }
                        editor.PointerUp(Pt(b)); Pump(4);
                        var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                        double areaBefore = GeomArea(ink.Data!);
                        int figBefore = Figures(ink.Data!);

                        // Vertical eraser sweep straight across the middle of the stroke.
                        eraserSizeField.SetValue(editor, 20.0);
                        Check("EraserToolBtn"); Pump(2);
                        double cx = (a.X + b.X) / 2;
                        double y0 = 0.5 * H - 60, y1 = 0.5 * H + 60;
                        editor.PointerDown(Pt(new Point(cx, y0))); Pump(1);
                        for (double y = y0 + stepPx; y <= y1; y += stepPx)
                            { editor.PointerMove(Pt(new Point(cx, y))); Pump(1); }
                        editor.PointerUp(Pt(new Point(cx, y1))); Pump(4);

                        bool survived = canvas.Children.Contains(ink);
                        double areaAfter = survived ? GeomArea(ink.Data!) : 0;
                        int figAfter = survived ? Figures(ink.Data!) : 0;
                        log.AppendLine($"ERASE penW={pw} step={stepPx} " +
                            $"figures {figBefore}->{figAfter} " +
                            $"area {areaBefore:F1}->{areaAfter:F1} " +
                            $"removed={areaBefore - areaAfter:F1} " +
                            $"expectedCut~{(20 + pw) * pw:F1} " +
                            $"cutClean={(figAfter == 2 ? "YES" : "NO")}");
                        if (stepPx == 60.0)
                            RenderToPng(editor, Path.Combine(outDir, $"audit-erase-w{pw}-fast.png"), dpi: 192);
                        if (stepPx == 0.5)
                            RenderToPng(editor, Path.Combine(outDir, $"audit-erase-w{pw}-slow.png"), dpi: 192);
                    }
                }

                // ---------- 3. eraser sweep ALONG a thin stroke (grazing) ----------
                foreach (double pw in new[] { 1.0, 2.0, 12.0 })
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, pw);
                    Check("FreehandToolBtn");
                    var a = new Point(0.08 * W, 0.5 * H);
                    var b = new Point(0.92 * W, 0.5 * H);
                    editor.PointerDown(Pt(a)); Pump(1);
                    for (int i = 1; i <= 120; i++)
                        { editor.PointerMove(Pt(new Point(a.X + (b.X - a.X) * i / 120.0, a.Y))); Pump(1); }
                    editor.PointerUp(Pt(b)); Pump(4);
                    var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    double areaBefore = GeomArea(ink.Data!);
                    eraserSizeField.SetValue(editor, 20.0);
                    Check("EraserToolBtn"); Pump(2);
                    // zig-zag along the stroke: many short segments, each barely overlapping
                    editor.PointerDown(Pt(new Point(0.20 * W, 0.5 * H))); Pump(1);
                    for (int i = 1; i <= 80; i++)
                    {
                        double x = 0.20 * W + (0.60 * W) * i / 80.0;
                        double y = 0.5 * H + ((i % 2 == 0) ? 9 : -9);
                        editor.PointerMove(Pt(new Point(x, y))); Pump(1);
                    }
                    editor.PointerUp(Pt(new Point(0.80 * W, 0.5 * H))); Pump(4);
                    bool survived = canvas.Children.Contains(ink);
                    log.AppendLine($"GRAZE penW={pw} survived={survived} " +
                        $"figures={(survived ? Figures(ink.Data!) : 0)} " +
                        $"area {areaBefore:F1}->{(survived ? GeomArea(ink.Data!) : 0):F1}");
                    RenderToPng(editor, Path.Combine(outDir, $"audit-erase-graze-w{pw}.png"), dpi: 192);
                }

                // ---------- 4. raw PaperInk.Subtract behaviour, no UI ----------
                foreach (double pw in new[] { 1.0, 2.0, 3.0, 12.0 })
                {
                    var pts = new List<Point>();
                    for (int i = 0; i <= 200; i++) pts.Add(new Point(50 + i * 4, 300));
                    var inkGeo = PaperInk.Outline(pts, pw);
                    double before = inkGeo.GetFlattenedPathGeometry().GetArea();
                    Geometry acc = inkGeo;
                    int noChange = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        var from = new Point(400, 240 + i * 3);
                        var to = new Point(400, 240 + (i + 1) * 3);
                        var cut = PaperInk.EraserStroke(new[] { from, to }, 20);
                        double a0 = acc.GetFlattenedPathGeometry().GetArea();
                        var next = PaperInk.Subtract(acc, cut) ?? acc;
                        next = PaperInk.CullSlivers(next, pw);
                        double a1 = next.GetFlattenedPathGeometry().GetArea();
                        if (Math.Abs(a0 - a1) < 1e-9) noChange++;
                        acc = next;
                    }
                    log.AppendLine($"RAW w={pw} area {before:F2} -> " +
                        $"{acc.GetFlattenedPathGeometry().GetArea():F2} " +
                        $"figures={acc.GetFlattenedPathGeometry().Figures.Count} " +
                        $"segmentsWithNoChange={noChange}/40 " +
                        $"widenFillRule={(PaperInk.Outline(pts, pw) as PathGeometry)!.FillRule}");
                }

                // ---------- 5. widened-outline fidelity: cap/join faceting ----------
                // Measure at the tolerance the EDITOR uses, not WPF's default. PaperInk.Outline
                // keeps its round caps/joins as PolyBezierSegments - exact arcs that the
                // rasterizer evaluates at device resolution, so the drawn stroke has no facets at
                // any width or zoom. Faceting only happens where something FLATTENS that outline
                // (Subtract, CullSlivers, TranslateGeometry), and those all pass PaperInk.Tolerance.
                // Reporting the default-flatten area here read as a 16%-thin 1px stroke that the
                // editor never draws, so print both and the segment types that settle it.
                foreach (double pw in new[] { 1.0, 3.0, 12.0, 30.0 })
                {
                    var dot = PaperInk.Outline(new[] { new Point(100, 100), new Point(101, 100) }, pw);
                    var atTol = dot.GetFlattenedPathGeometry(PaperInk.Tolerance, ToleranceType.Absolute);
                    var segs = string.Join("+", dot.Figures.SelectMany(f => f.Segments)
                        .Select(s => s.GetType().Name).Distinct());
                    log.AppendLine($"CAP w={pw} segs={segs} bounds={dot.Bounds} " +
                        $"editorVerts={FlatVertexCount(atTol)} editorArea={atTol.GetArea():F3} " +
                        $"wpfDefaultVerts={FlatVertexCount(dot)} " +
                        $"wpfDefaultArea={dot.GetFlattenedPathGeometry().GetArea():F3} " +
                        $"idealArea={(Math.PI * pw * pw / 4 + pw):F3}");
                }

                // ---------- 6. highlighter fidelity vs the exact CSS multiply ----------
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, 12.0);
                    Check("HighlighterToolBtn");
                    // Straight across the card text row "Arrow Shape Pen Text". Find it rather than
                    // hard-coding a y: the highlight has to LAND ON glyphs for the blend's
                    // resampling to be visible at all.
                    int textY = 186, bestInk = -1;
                    for (int y = 140; y < 260; y++)
                    {
                        int dark = 0;
                        for (int x = 100; x <= 320; x++)
                            if (source.GetPixel(x, y).R < 140) dark++;
                        if (dark > bestInk) { bestInk = dark; textY = y; }
                    }
                    var ha = new Point(85, textY); var hb = new Point(340, textY);
                    editor.PointerDown(Pt(ha)); Pump(1);
                    for (int i = 1; i <= 60; i++)
                        { editor.PointerMove(Pt(new Point(ha.X + (hb.X - ha.X) * i / 60.0, ha.Y))); Pump(1); }
                    editor.PointerUp(Pt(hb)); Pump(6);
                    var hl = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    var hostField = typeof(EditorWindow).GetProperty("CanvasHost",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var host = (FrameworkElement)editor.FindName("CanvasHost")!;
                    using var live = BitmapEffects.RenderVisual(host, source.Width, source.Height);
                    var c = AnnotationStyle.HighlighterColor;
                    // Compare the live canvas inside the ink to page*color, and probe a few
                    // integer offsets to reveal any misregistration of the ImageBrush.
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        long err = 0; int n2 = 0; int worst = 0;
                        for (int y = textY - 5; y <= textY + 5; y++)
                            for (int x = 100; x <= 320; x++)
                            {
                                var got = live.GetPixel(x, y);
                                var src = source.GetPixel(x, Math.Clamp(y + dy, 0, source.Height - 1));
                                int e = Math.Abs(got.R - HighlighterBlend.MultiplyChannel(src.R, c.R, c.A))
                                      + Math.Abs(got.G - HighlighterBlend.MultiplyChannel(src.G, c.G, c.A))
                                      + Math.Abs(got.B - HighlighterBlend.MultiplyChannel(src.B, c.B, c.A));
                                err += e; n2++; if (e > worst) worst = e;
                            }
                        log.AppendLine($"HL-FIDELITY dy={dy} meanAbsErr={(double)err / n2 / 3:F2} worstPixelErr={worst}");
                    }
                    log.AppendLine($"HL textY={textY} bounds={hl.Data!.Bounds} fill={hl.Fill.GetType().Name} " +
                        $"scaling={RenderOptions.GetBitmapScalingMode(hl.Fill)} " +
                        $"baseTilesScaling={RenderOptions.GetBitmapScalingMode(((Panel)editor.FindName("BaseTiles")!).Children[0])} " +
                        $"width={((AnnotationData)hl.Tag!).Thickness} color={((AnnotationData)hl.Tag!).Color} " +
                        $"host={hostField?.Name}");
                    RenderToPng(editor, Path.Combine(outDir, "audit-highlighter-over-text-200pct.png"), dpi: 192);

                    // The blend is baked into an ImageBrush, so WPF resamples it through the zoom
                    // transform. Survey's browser evaluates mix-blend-mode per DEVICE pixel, so the
                    // content under a highlight is exactly as crisp as the content beside it. The
                    // closest WPF gets is using the SAME filter as the page (BaseTiles = Fant /
                    // HighQuality); the brush shipped on the default Linear. Render both at 300% so
                    // the difference is eyeballable rather than argued about.
                    // Anchor on the highlight itself so zooming leaves it under the same viewport
                    // point instead of scrolling it off screen.
                    typeof(EditorWindow).GetMethod("ZoomAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object[]
                        {
                            canvas.TranslatePoint(new Point(210, textY), viewport), 3.0,
                        });
                    Pump(6);
                    foreach (var mode in new[] { BitmapScalingMode.Linear, BitmapScalingMode.HighQuality })
                    {
                        RenderOptions.SetBitmapScalingMode(hl.Fill, mode);
                        Pump(4);
                        RenderToPng(editor, Path.Combine(outDir, $"audit-highlighter-zoom300-{mode}.png"), dpi: 96);
                    }
                    InvokePrivate(editor, "FitToView");
                    Pump(4);
                }

                // ---------- 7. eraser on a MOVED stroke (nonzero element offset) ----------
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, 6.0);
                    Check("FreehandToolBtn");
                    var a = new Point(0.10 * W, 0.30 * H);
                    var b = new Point(0.85 * W, 0.30 * H);
                    editor.PointerDown(Pt(a)); Pump(1);
                    for (int i = 1; i <= 60; i++)
                        { editor.PointerMove(Pt(new Point(a.X + (b.X - a.X) * i / 60.0, a.Y))); Pump(1); }
                    editor.PointerUp(Pt(b)); Pump(4);
                    var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    // Select and drag it down 120 px so ElementOffset != 0.
                    Check("SelectToolBtn"); Pump(2);
                    editor.PointerDown(Pt(new Point(0.5 * W, 0.30 * H))); Pump(1);
                    for (int i = 1; i <= 20; i++)
                        { editor.PointerMove(Pt(new Point(0.5 * W, 0.30 * H + 120.0 * i / 20.0))); Pump(1); }
                    editor.PointerUp(Pt(new Point(0.5 * W, 0.30 * H + 120))); Pump(4);
                    InvokePrivate(editor, "Select", null); Pump(2);
                    double areaBefore = GeomArea(ink.Data!);
                    var bnds = ink.Data!.Bounds;
                    eraserSizeField.SetValue(editor, 20.0);
                    Check("EraserToolBtn"); Pump(2);
                    double ey = 0.30 * H + 120;
                    editor.PointerDown(Pt(new Point(0.5 * W, ey - 60))); Pump(1);
                    for (int i = 1; i <= 40; i++)
                        { editor.PointerMove(Pt(new Point(0.5 * W, ey - 60 + 120.0 * i / 40.0))); Pump(1); }
                    editor.PointerUp(Pt(new Point(0.5 * W, ey + 60))); Pump(5);
                    bool survived = canvas.Children.Contains(ink);
                    log.AppendLine($"MOVED-ERASE survived={survived} dataBounds={bnds}->" +
                        $"{(survived ? ink.Data!.Bounds.ToString() : "gone")} " +
                        $"figures={(survived ? Figures(ink.Data!) : 0)} " +
                        $"area {areaBefore:F1}->{(survived ? GeomArea(ink.Data!) : 0):F1}");
                    RenderToPng(editor, Path.Combine(outDir, "audit-erase-moved.png"), dpi: 192);
                }

                // ---------- 8. many short passes over one self-crossing stroke ----------
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, 8.0);
                    Check("FreehandToolBtn");
                    var loop = new List<Point>();
                    for (int i = 0; i <= 160; i++)
                    {
                        double t = i / 160.0 * Math.PI * 2;
                        loop.Add(new Point(0.5 * W + 0.22 * W * Math.Cos(t) + 0.06 * W * Math.Cos(3 * t),
                                           0.5 * H + 0.28 * H * Math.Sin(t)));
                    }
                    editor.PointerDown(Pt(loop[0])); Pump(1);
                    for (int i = 1; i < loop.Count; i++) { editor.PointerMove(Pt(loop[i])); Pump(1); }
                    editor.PointerUp(Pt(loop[^1])); Pump(4);
                    var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    double a0 = GeomArea(ink.Data!);
                    eraserSizeField.SetValue(editor, 16.0);
                    Check("EraserToolBtn"); Pump(2);
                    int passes = 0, misses = 0;
                    for (int k = 0; k < 12 && canvas.Children.Contains(ink); k++)
                    {
                        double y = 0.20 * H + k * 0.05 * H;
                        double before = GeomArea(ink.Data!);
                        editor.PointerDown(Pt(new Point(0.10 * W, y))); Pump(1);
                        for (int i = 1; i <= 30; i++)
                            { editor.PointerMove(Pt(new Point(0.10 * W + 0.80 * W * i / 30.0, y))); Pump(1); }
                        editor.PointerUp(Pt(new Point(0.90 * W, y))); Pump(4);
                        passes++;
                        if (canvas.Children.Contains(ink) && Math.Abs(GeomArea(ink.Data!) - before) < 1e-6)
                            misses++;
                    }
                    bool survived = canvas.Children.Contains(ink);
                    log.AppendLine($"MULTIPASS passes={passes} passesThatCutNothing={misses} " +
                        $"survived={survived} figures={(survived ? Figures(ink.Data!) : 0)} " +
                        $"area {a0:F1}->{(survived ? GeomArea(ink.Data!) : 0):F1}");
                    RenderToPng(editor, Path.Combine(outDir, "audit-erase-multipass.png"), dpi: 192);
                }

                // ---------- 9. SAMPLE-RATE DETERMINISM ----------
                // Survey's carve is a pure function of (original geometry, whole gesture
                // polyline, radius) because it runs ONCE at pointer-up over the full point list
                // (FabricEraserCanvas.jsx:1820-1846). Feeding the SAME wavy path with extra
                // samples that lie on it (1x/2x/8x subdivision) leaves the swept region
                // identical, so the surviving ink must be identical too. When every pointer
                // move ran its own subtract + sliver cull, it was not.
                var waveBase = Enumerable.Range(0, 25)
                    .Select(i => new Point(0.15 * W + 0.70 * W * i / 24.0,
                                           0.5 * H + 26 * Math.Sin(i / 24.0 * Math.PI * 5))).ToList();
                foreach (int sub in new[] { 1, 2, 8 })
                {
                    // Every extra sample sits ON a base segment, so the swept region is unchanged.
                    var wave = new List<Point> { waveBase[0] };
                    for (int i = 1; i < waveBase.Count; i++)
                        for (int k = 1; k <= sub; k++)
                            wave.Add(new Point(
                                waveBase[i - 1].X + (waveBase[i].X - waveBase[i - 1].X) * k / sub,
                                waveBase[i - 1].Y + (waveBase[i].Y - waveBase[i - 1].Y) * k / sub));

                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, 8.0);
                    Check("FreehandToolBtn");
                    var a = new Point(0.08 * W, 0.5 * H);
                    var b = new Point(0.92 * W, 0.5 * H);
                    editor.PointerDown(Pt(a)); Pump(1);
                    for (int i = 1; i <= 120; i++)
                        { editor.PointerMove(Pt(new Point(a.X + (b.X - a.X) * i / 120.0, a.Y))); Pump(1); }
                    editor.PointerUp(Pt(b)); Pump(4);
                    var ink = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    double a0 = GeomArea(ink.Data!);

                    eraserSizeField.SetValue(editor, 18.0);
                    Check("EraserToolBtn"); Pump(2);
                    editor.PointerDown(Pt(wave[0])); Pump(1);
                    for (int i = 1; i < wave.Count; i++) { editor.PointerMove(Pt(wave[i])); Pump(1); }
                    editor.PointerUp(Pt(wave[^1])); Pump(5);
                    bool survived = canvas.Children.Contains(ink);
                    log.AppendLine($"SAMPLERATE subdiv={sub} samples={wave.Count} survived={survived} " +
                        $"figures={(survived ? Figures(ink.Data!) : 0)} " +
                        $"area {a0:F1}->{(survived ? GeomArea(ink.Data!) : 0):F2}");
                    RenderToPng(editor, Path.Combine(outDir, $"audit-erase-samplerate-x{sub}.png"), dpi: 192);
                }

                // ---------- 10. ROUND-2 PROBES ----------
                var eraseBeforeField = typeof(EditorWindow).GetField("_eraseBefore",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var eraseRemovedField = typeof(EditorWindow).GetField("_eraseRemoved",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                int EraseBeforeCount() =>
                    ((System.Collections.ICollection)eraseBeforeField.GetValue(editor)!).Count;
                int EraseRemovedCount() =>
                    ((System.Collections.ICollection)eraseRemovedField.GetValue(editor)!).Count;
                int UndoDepth()
                {
                    var f = typeof(EditorWindow).GetField("_undo",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    return f?.GetValue(editor) is System.Collections.ICollection c ? c.Count : -1;
                }
                System.Windows.Shapes.Path DrawPen(double w, Point a, Point b, int steps)
                {
                    penSizeField.SetValue(editor, w);
                    Check("FreehandToolBtn");
                    editor.PointerDown(Pt(a)); Pump(1);
                    for (int i = 1; i <= steps; i++)
                        editor.PointerMove(Pt(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps)));
                    Pump(1);
                    editor.PointerUp(Pt(b)); Pump(4);
                    return canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                }

                // 10A. ABORT MID-GESTURE (Survey commits the interrupted erase;
                //      WinShot's LostMouseCapture -> AbortDrag drops it and keeps state).
                {
                    canvas.Children.Clear(); Pump(3);
                    var s1 = DrawPen(8, new Point(0.10 * W, 0.30 * H), new Point(0.90 * W, 0.30 * H), 60);
                    var s2 = DrawPen(8, new Point(0.10 * W, 0.60 * H), new Point(0.90 * W, 0.60 * H), 60);
                    double s1Full = GeomArea(s1.Data!), s2Full = GeomArea(s2.Data!);
                    eraserSizeField.SetValue(editor, 24.0);
                    Check("EraserToolBtn"); Pump(2);
                    int undo0 = UndoDepth();
                    // Gesture 1 over s1, interrupted (never released).
                    editor.PointerDown(Pt(new Point(0.50 * W, 0.30 * H - 50))); Pump(1);
                    for (int i = 1; i <= 40; i++)
                        editor.PointerMove(Pt(new Point(0.50 * W, 0.30 * H - 50 + 100.0 * i / 40)));
                    Pump(2);
                    double s1Mid = GeomArea(s1.Data!);
                    RenderToPng(editor, Path.Combine(outDir, "r2-abort-mid.png"), dpi: 96);
                    InvokePrivate(editor, "AbortDrag"); Pump(3);
                    double s1AfterAbort = GeomArea(s1.Data!);
                    int undo1 = UndoDepth();
                    int leakBefore = EraseBeforeCount(), leakRemoved = EraseRemovedCount();
                    // Gesture 2 over s2 only.
                    editor.PointerDown(Pt(new Point(0.30 * W, 0.60 * H - 50))); Pump(1);
                    for (int i = 1; i <= 40; i++)
                        editor.PointerMove(Pt(new Point(0.30 * W, 0.60 * H - 50 + 100.0 * i / 40)));
                    Pump(1);
                    editor.PointerUp(Pt(new Point(0.30 * W, 0.60 * H + 50))); Pump(5);
                    double s1AfterG2 = GeomArea(s1.Data!), s2AfterG2 = GeomArea(s2.Data!);
                    RenderToPng(editor, Path.Combine(outDir, "r2-abort-after-next-gesture.png"), dpi: 96);
                    log.AppendLine($"R2-ABORT s1 full={s1Full:F1} mid={s1Mid:F1} afterAbort={s1AfterAbort:F1} " +
                        $"afterUnrelatedGesture={s1AfterG2:F1} RESURRECTED={(s1AfterG2 > s1AfterAbort + 1)} " +
                        $"undoDepth {undo0}->{undo1} (abort pushed {undo1 - undo0}) " +
                        $"leaked _eraseBefore={leakBefore} _eraseRemoved={leakRemoved} " +
                        $"s2 {s2Full:F1}->{s2AfterG2:F1}");
                }

                // 10B. GATE STRESS: does the exact per-segment Intersect ever say "no"
                //      when the same cut actually removes ink? (Survey's gate is a padded AABB
                //      superset; WinShot's is an exact boolean whose verdict also decides
                //      whether the stroke is carved at all on pointer-up.)
                {
                    var rng = new Random(20260904);
                    int gateNoButCuts = 0, gateThrew = 0, gateNull = 0, trials = 0;
                    foreach (double pw in new[] { 1.0, 2.0, 3.0, 8.0 })
                    {
                        var pts = new List<Point>();
                        for (int i = 0; i <= 60; i++)
                            pts.Add(new Point(60 + i * 6, 300 + 40 * Math.Sin(i / 60.0 * Math.PI * 4)));
                        var inkGeo = PaperInk.Outline(pts, pw);
                        for (int k = 0; k < 220; k++)
                        {
                            trials++;
                            var p0 = pts[rng.Next(pts.Count)];
                            // Offsets that graze the stroke edge, where a tangent intersection
                            // is a degenerate sliver.
                            double d = 12;
                            var from = new Point(p0.X + (rng.NextDouble() - 0.5) * 4,
                                                 p0.Y + (rng.NextDouble() * 2 - 1) * (d / 2 + pw / 2));
                            var to = new Point(from.X + (rng.NextDouble() - 0.5) * 30,
                                               from.Y + (rng.NextDouble() - 0.5) * 4);
                            var cut = PaperInk.EraserStroke(new[] { from, to }, d);
                            bool gate;
                            try
                            {
                                var inter = Geometry.Combine(inkGeo, cut, GeometryCombineMode.Intersect,
                                    Transform.Identity, PaperInk.Tolerance, ToleranceType.Absolute);
                                if (inter is null) { gateNull++; gate = false; }
                                else gate = !inter.IsEmpty();
                            }
                            catch { gateThrew++; gate = false; }
                            var sub = PaperInk.Subtract(inkGeo, cut);
                            bool cuts = sub is not null &&
                                inkGeo.GetFlattenedPathGeometry(PaperInk.Tolerance, ToleranceType.Absolute).GetArea()
                                - sub.GetFlattenedPathGeometry(PaperInk.Tolerance, ToleranceType.Absolute).GetArea() > 1e-6;
                            if (!gate && cuts) gateNoButCuts++;
                        }
                    }
                    log.AppendLine($"R2-GATE trials={trials} gateSaidNoButCutRemovesInk={gateNoButCuts} " +
                        $"combineThrew={gateThrew} combineReturnedNull={gateNull}");
                }

                // 10C. MANY SEPARATE STROKES, ONE FAST GESTURE. Survey carves every annotation
                //      whose padded bounds meet the gesture; WinShot only carves what the
                //      per-segment gate happened to flag.
                {
                    canvas.Children.Clear(); Pump(3);
                    var strokes = new List<System.Windows.Shapes.Path>();
                    for (int i = 0; i < 14; i++)
                    {
                        double x = 0.10 * W + 0.055 * W * i;
                        strokes.Add(DrawPen(1, new Point(x, 0.25 * H), new Point(x, 0.75 * H), 20));
                    }
                    var areasBefore = strokes.Select(s => GeomArea(s.Data!)).ToList();
                    eraserSizeField.SetValue(editor, 18.0);
                    Check("EraserToolBtn"); Pump(2);
                    // 3 pointer samples only: a genuinely fast swipe.
                    editor.PointerDown(Pt(new Point(0.05 * W, 0.50 * H))); Pump(1);
                    editor.PointerMove(Pt(new Point(0.50 * W, 0.52 * H))); Pump(1);
                    editor.PointerMove(Pt(new Point(0.95 * W, 0.50 * H))); Pump(1);
                    editor.PointerUp(Pt(new Point(0.95 * W, 0.50 * H))); Pump(6);
                    int carved = 0, untouched = 0;
                    for (int i = 0; i < strokes.Count; i++)
                    {
                        bool alive = canvas.Children.Contains(strokes[i]);
                        double a1 = alive ? GeomArea(strokes[i].Data!) : 0;
                        if (!alive || a1 < areasBefore[i] - 1e-6) carved++; else untouched++;
                    }
                    log.AppendLine($"R2-FASTSWIPE strokes={strokes.Count} carved={carved} untouched={untouched} " +
                        "(every stroke crosses y=0.5H, so untouched must be 0)");
                    RenderToPng(editor, Path.Combine(outDir, "r2-fastswipe.png"), dpi: 96);
                }

                // 10D. HOLLOW SHAPE INTERIOR. Survey's isPointOnRect only hits an unfilled rect
                //      near its BORDER (geometryHitTest.js:902-925); WinShot's HitsEraser tests
                //      the solid bounding RectangleGeometry.
                {
                    canvas.Children.Clear(); Pump(3);
                    Check("RectangleToolBtn");
                    editor.PointerDown(Pt(new Point(0.15 * W, 0.20 * H))); Pump(1);
                    for (int i = 1; i <= 10; i++)
                        editor.PointerMove(Pt(new Point(0.15 * W + 0.55 * W * i / 10.0, 0.20 * H + 0.55 * H * i / 10.0)));
                    editor.PointerUp(Pt(new Point(0.70 * W, 0.75 * H))); Pump(4);
                    Check("EllipseToolBtn");
                    editor.PointerDown(Pt(new Point(0.75 * W, 0.20 * H))); Pump(1);
                    for (int i = 1; i <= 10; i++)
                        editor.PointerMove(Pt(new Point(0.75 * W + 0.20 * W * i / 10.0, 0.20 * H + 0.55 * H * i / 10.0)));
                    editor.PointerUp(Pt(new Point(0.95 * W, 0.75 * H))); Pump(4);
                    InvokePrivate(editor, "Select", null); Pump(2);
                    int shapesBefore = canvas.Children.OfType<FrameworkElement>()
                        .Count(f => f.Tag is AnnotationData { Type: AnnotationData.TypeRectangle or AnnotationData.TypeEllipse });
                    string fills = string.Join(",", canvas.Children.OfType<System.Windows.Shapes.Shape>()
                        .Where(s => s.Tag is AnnotationData)
                        .Select(s => (s.Fill as SolidColorBrush)?.Color.ToString() ?? s.Fill?.GetType().Name ?? "null"));
                    eraserSizeField.SetValue(editor, 10.0);
                    Check("EraserToolBtn"); Pump(2);
                    // Straight through the empty middle of the rect; never near a border.
                    editor.PointerDown(Pt(new Point(0.30 * W, 0.45 * H))); Pump(1);
                    for (int i = 1; i <= 10; i++)
                        editor.PointerMove(Pt(new Point(0.30 * W + 0.20 * W * i / 10.0, 0.45 * H)));
                    editor.PointerUp(Pt(new Point(0.50 * W, 0.45 * H))); Pump(5);
                    int afterRect = canvas.Children.OfType<FrameworkElement>()
                        .Count(f => f.Tag is AnnotationData { Type: AnnotationData.TypeRectangle or AnnotationData.TypeEllipse });
                    // Ellipse: the top-left CORNER of its bbox, outside the ellipse itself.
                    editor.PointerDown(Pt(new Point(0.757 * W, 0.213 * H))); Pump(1);
                    editor.PointerMove(Pt(new Point(0.762 * W, 0.218 * H))); Pump(1);
                    editor.PointerUp(Pt(new Point(0.762 * W, 0.218 * H))); Pump(5);
                    int afterEllipse = canvas.Children.OfType<FrameworkElement>()
                        .Count(f => f.Tag is AnnotationData { Type: AnnotationData.TypeRectangle or AnnotationData.TypeEllipse });
                    log.AppendLine($"R2-HOLLOW fills=[{fills}] shapes={shapesBefore} " +
                        $"afterInteriorSwipe={afterRect} afterEllipseCornerTap={afterEllipse}");
                    RenderToPng(editor, Path.Combine(outDir, "r2-hollow.png"), dpi: 96);
                }

                // 10E. HIGHLIGHTER AT ZOOM. Multiply is per-pixel AFFINE in the backdrop, so a
                //      correct blend scales local contrast by exactly (1-a+a*Cs) and loses no
                //      sharpness. Measure glyph-edge contrast at 300% with and without the
                //      highlighter over the same text, and compare to that factor.
                {
                    canvas.Children.Clear(); Pump(3);
                    penSizeField.SetValue(editor, 12.0);
                    int textY = 186, bestInk = -1;
                    for (int y = 140; y < 260; y++)
                    {
                        int dark = 0;
                        for (int x = 100; x <= 320; x++) if (source.GetPixel(x, y).R < 140) dark++;
                        if (dark > bestInk) { bestInk = dark; textY = y; }
                    }
                    Check("HighlighterToolBtn");
                    var ha = new Point(85, textY); var hb = new Point(340, textY);
                    editor.PointerDown(Pt(ha)); Pump(1);
                    for (int i = 1; i <= 40; i++)
                        editor.PointerMove(Pt(new Point(ha.X + (hb.X - ha.X) * i / 40.0, ha.Y)));
                    editor.PointerUp(Pt(hb)); Pump(6);
                    var hl = canvas.Children.OfType<System.Windows.Shapes.Path>().Last();
                    var host = (FrameworkElement)editor.FindName("CanvasHost")!;
                    InvokePrivate(editor, "FitToView"); Pump(4);
                    typeof(EditorWindow).GetMethod("ZoomAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object[] { canvas.TranslatePoint(new Point(210, textY), viewport), 3.0 });
                    Pump(8);
                    double Contrast(SD.Bitmap bmp, Rect r)
                    {
                        double sum = 0; int n3 = 0;
                        for (int y = (int)r.Top; y < (int)r.Bottom; y++)
                            for (int x = (int)r.Left; x < (int)r.Right - 1; x++)
                            { sum += Math.Abs(bmp.GetPixel(x, y).G - bmp.GetPixel(x + 1, y).G); n3++; }
                        return n3 == 0 ? 0 : sum / n3;
                    }
                    // Screen rect of the highlighted band, in VIEWPORT pixels.
                    var b0 = hl.Data!.Bounds;
                    Point tl = canvas.TranslatePoint(new Point(b0.Left, b0.Top), viewport);
                    Point br2 = canvas.TranslatePoint(new Point(b0.Right, b0.Bottom), viewport);
                    var band = new Rect(
                        Math.Max(1, tl.X), Math.Max(1, tl.Y + 2),
                        Math.Max(2, Math.Min(br2.X, viewport.ActualWidth - 2) - Math.Max(1, tl.X)),
                        Math.Max(2, Math.Min(br2.Y, viewport.ActualHeight - 2) - Math.Max(1, tl.Y + 2)));
                    using var withHl = BitmapEffects.RenderVisual(viewport,
                        (int)viewport.ActualWidth, (int)viewport.ActualHeight);
                    hl.Visibility = Visibility.Hidden; Pump(4);
                    using var noHl = BitmapEffects.RenderVisual(viewport,
                        (int)viewport.ActualWidth, (int)viewport.ActualHeight);
                    hl.Visibility = Visibility.Visible; Pump(3);
                    var c2 = AnnotationStyle.HighlighterColor;
                    double factorG = 1 - c2.A / 255.0 + (c2.A / 255.0) * (c2.G / 255.0);
                    double cWith = Contrast(withHl, band), cWithout = Contrast(noHl, band);
                    log.AppendLine($"R2-HLZOOM band={band} contrastNoHl={cWithout:F3} contrastWithHl={cWith:F3} " +
                        $"ratio={(cWithout > 0 ? cWith / cWithout : 0):F4} exactMultiplyFactorG={factorG:F4} " +
                        $"sharpnessLoss={(cWithout > 0 ? 1 - (cWith / cWithout) / factorG : 0):P1}");
                    RenderToPng(editor, Path.Combine(outDir, "r2-hl-zoom300.png"), dpi: 96);
                    InvokePrivate(editor, "FitToView"); Pump(4);
                }

                File.WriteAllText(Path.Combine(outDir, "ink-eraser-audit.txt"), log.ToString());
                editor.Close();
                Pump(3);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static int FlatVertexCount(Geometry g)
    {
        int n = 0;
        foreach (var f in g.GetFlattenedPathGeometry().Figures)
        {
            n += 1;
            foreach (var s in f.Segments)
            {
                if (s is LineSegment) n += 1;
                else if (s is PolyLineSegment pls) n += pls.Points.Count;
            }
        }
        return n;
    }

    /// <summary>
    /// Drives every drawing tool through the editor's pointer seam (no real mouse, no
    /// visible window) and renders each state, so tool behaviour can be compared against
    /// Survey without touching the user's desktop. Gated like the render above.
    /// </summary>
    [Fact]
    public void RenderPointerDrivenToolBaseline()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_POINTER") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-annotation-render");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Effects await background work; route continuations back to this STA thread so Pump runs them.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var source = CreateSanitizedSource();
                var editor = new EditorWindow(source, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                Pump(45);

                var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;
                double W = source.Width, H = source.Height;

                EditorWindow.PointerArgs P(double fx, double fy, int clicks = 1)
                {
                    var c = new Point(fx * W, fy * H);
                    return new EditorWindow.PointerArgs(c, canvas.TranslatePoint(c, viewport), MouseButton.Left, clicks);
                }
                void Click(double fx, double fy, int clicks = 1)
                {
                    editor.PointerDown(P(fx, fy, clicks)); Pump(1);
                    editor.PointerUp(P(fx, fy)); Pump(3);
                }
                void Drag(IList<(double fx, double fy)> pts, int steps = 12)
                {
                    editor.PointerDown(P(pts[0].fx, pts[0].fy)); Pump(1);
                    for (int k = 1; k < pts.Count; k++)
                        for (int i = 1; i <= steps; i++)
                        {
                            double t = i / (double)steps;
                            editor.PointerMove(P(pts[k - 1].fx + (pts[k].fx - pts[k - 1].fx) * t,
                                                 pts[k - 1].fy + (pts[k].fy - pts[k - 1].fy) * t));
                            Pump(1);
                        }
                    editor.PointerUp(P(pts[^1].fx, pts[^1].fy)); Pump(3);
                }
                void Check(string name) { ((RadioButton)editor.FindName(name)!).IsChecked = true; Pump(3); }
                void Shot(string name) => RenderToPng(editor, Path.Combine(outDir, name));

                // Fit check: the whole image must sit inside the viewport on open.
                {
                    Point tl = canvas.TranslatePoint(new Point(0, 0), viewport);
                    Point brPt = canvas.TranslatePoint(new Point(W, H), viewport);
                    File.WriteAllText(Path.Combine(outDir, "fit.txt"),
                        $"image={tl}..{brPt} viewport={viewport.ActualWidth}x{viewport.ActualHeight}");
                }
                Shot("pointer-open.png");

                // LAYOUT: Survey's sub-row is a PORTAL that only mounts while a category is
                // open (PDFViewer.jsx:32015), it OVERLAYS the canvas (AppShell.jsx:2727-2740),
                // and the chip is a TOGGLE (AppShell.jsx:1637-1641). Scan the window's left
                // edge column for the chrome bands so the three claims are measurable.
                var groupBar = (FrameworkElement)editor.FindName("EditorGroupBar")!;
                string ScanColumn(int x = 4)
                {
                    editor.UpdateLayout();
                    var bmp = new RenderTargetBitmap(
                        (int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    bmp.Render(editor);
                    int h = bmp.PixelHeight;
                    var px = new byte[h * 4];
                    bmp.CopyPixels(new Int32Rect(x, 0, 1, h), px, 4, 0);
                    var runs = new List<string>();
                    string prev = ""; int start = 0;
                    for (int y = 0; y <= h; y++)
                    {
                        string c = y == h ? "" : $"#{px[y * 4 + 2]:X2}{px[y * 4 + 1]:X2}{px[y * 4 + 0]:X2}";
                        if (c != prev)
                        {
                            if (prev.Length > 0) runs.Add($"{prev} y={start}..{y - 1}");
                            prev = c; start = y;
                        }
                    }
                    return string.Join(" | ", runs);
                }
                double viewportHOnOpen = viewport.ActualHeight;
                var layoutLog = new System.Text.StringBuilder();
                layoutLog.AppendLine($"select/no-group: band={groupBar.Visibility} viewportH={viewport.ActualHeight:0.##}");
                layoutLog.AppendLine($"  column: {ScanColumn()}");

                // Chip opens the Draw category.
                ((Button)editor.FindName("DrawGroupBtn")!).RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(4);
                Shot("layout-subrow-open.png");
                layoutLog.AppendLine($"draw open: band={groupBar.Visibility} barH={groupBar.ActualHeight} " +
                    $"viewportH={viewport.ActualHeight:0.##} (delta={viewport.ActualHeight - viewportHOnOpen:0.##})");
                layoutLog.AppendLine($"  column: {ScanColumn()}");

                // Clicking the ACTIVE chip closes the row and leaves the tool alone.
                object toolWhileOpen = typeof(EditorWindow).GetField("_tool",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                ((Button)editor.FindName("DrawGroupBtn")!).RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(4);
                Shot("layout-subrow-closed.png");
                object toolAfterClose = typeof(EditorWindow).GetField("_tool",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                layoutLog.AppendLine($"chip toggled shut: band={groupBar.Visibility} tool {toolWhileOpen}->{toolAfterClose} " +
                    $"viewportH={viewport.ActualHeight:0.##}");
                layoutLog.AppendLine($"  column: {ScanColumn()}");
                File.WriteAllText(Path.Combine(outDir, "layout-chrome.txt"), layoutLog.ToString());

                // LAYOUT-04: Survey's rail is ONE 48px #12151c column under the top bar with
                // exactly one horizontal rule — its own footer's (SurveySpacesRail.jsx:1222-1231,
                // AppShell.jsx:3011). Scanning the rail column must show a single #2A3140 run,
                // and the fill must reach the window's last row.
                var railLog = new System.Text.StringBuilder();
                railLog.AppendLine($"rail column x=w-20: {ScanColumn((int)editor.ActualWidth - 20)}");
                railLog.AppendLine($"chip centring: PanSelectPanel right edge vs ToolPanel left edge / window centre");
                var panSelect = (FrameworkElement)editor.FindName("PanSelectPanel")!;
                var toolPanel = (FrameworkElement)editor.FindName("ToolPanel")!;
                editor.UpdateLayout();
                double psL = panSelect.TransformToAncestor(editor).Transform(new Point(0, 0)).X;
                double tpL = toolPanel.TransformToAncestor(editor).Transform(new Point(0, 0)).X;
                railLog.AppendLine($"  panSelect=[{psL:0.#}..{psL + panSelect.ActualWidth:0.#}] " +
                    $"chips=[{tpL:0.#}..{tpL + toolPanel.ActualWidth:0.#}] " +
                    $"chipsCentre={tpL + toolPanel.ActualWidth / 2:0.#} windowCentre={editor.ActualWidth / 2:0.#}");
                File.WriteAllText(Path.Combine(outDir, "layout-rail.txt"), railLog.ToString());
                Check("SelectToolBtn");

                // Shapes: rect, ellipse, line, arrow, two counters.
                Check("RectangleToolBtn"); Drag(new[] { (0.10, 0.25), (0.36, 0.52) });
                Check("EllipseToolBtn"); Drag(new[] { (0.42, 0.25), (0.68, 0.52) });
                Check("LineToolBtn"); Drag(new[] { (0.10, 0.62), (0.36, 0.85) });
                Check("ArrowToolBtn"); Drag(new[] { (0.42, 0.62), (0.68, 0.85) });
                var counterBtn = ((StackPanel)editor.FindName("ShapeGroupPanel")!).Children
                    .OfType<RadioButton>().First(rb => rb.Tag as string == "Step");
                counterBtn.IsChecked = true; Pump(3);
                Click(0.80, 0.35); Click(0.80, 0.72);
                Shot("pointer-shapes.png");

                Check("SelectToolBtn");
                Click(0.10, 0.40); Shot("pointer-rect-selected.png");
                // LAYOUT-01/02: with Select armed, Survey's category strip is force-closed and no
                // chip is gold, whatever is selected (PDFViewer.jsx:4558-4568, AppShell.jsx:1647).
                {
                    var shapeChip = (Button)editor.FindName("ShapeGroupBtn")!;
                    File.WriteAllText(Path.Combine(outDir, "layout-select-chrome.txt"),
                        $"select+rect selected: band={groupBar.Visibility} " +
                        $"shapeChipFg={(shapeChip.Foreground as SolidColorBrush)?.Color} " +
                        $"goldBrush={((SolidColorBrush)editor.FindResource("SurveyGoldBrush")).Color}");
                }
                Click(0.55, 0.735); Shot("pointer-arrow-selected.png");
                // Survey's curve-bend midpoint carries a <title>Drag to bend</title>
                // (SVGAnnotationLayer.jsx:5374-5463). WinShot's handle layer is
                // IsHitTestVisible=False, so the chip is driven from the hover hit-test.
                editor.PointerMove(P(0.55, 0.735)); Pump(3);
                var bendTipField = typeof(EditorWindow).GetField("_bendTip",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var bendTip = (System.Windows.Controls.ToolTip?)bendTipField.GetValue(editor);
                string bendOnMidpoint = $"open={bendTip?.IsOpen} content={bendTip?.Content}";
                editor.PointerMove(P(0.42, 0.62)); Pump(3);   // an ENDPOINT: no bend chip
                bendTip = (System.Windows.Controls.ToolTip?)bendTipField.GetValue(editor);
                File.WriteAllText(Path.Combine(outDir, "bend-tooltip.txt"),
                    $"midpoint: {bendOnMidpoint}" + Environment.NewLine +
                    $"endpoint: open={bendTip?.IsOpen}");
                if (bendTip is not null) bendTip.IsOpen = false;
                Click(0.80, 0.35); Shot("pointer-counter-selected.png");
                Click(0.02, 0.02);

                // Pen: a sine wave with slow and fast sections, then a loop.
                Check("FreehandToolBtn");
                var wave = new List<(double, double)>();
                for (int i = 0; i <= 40; i++) wave.Add((0.10 + 0.30 * i / 40.0, 0.15 + 0.06 * Math.Sin(i / 40.0 * Math.PI * 2)));
                Drag(wave, steps: 2);
                var loop = new List<(double, double)>();
                for (int i = 0; i <= 60; i++) { double t = i / 60.0 * Math.PI * 2; loop.Add((0.62 + 0.10 * Math.Cos(t), 0.15 + 0.09 * Math.Sin(t))); }
                Drag(loop, steps: 1);
                Shot("pointer-pen.png");
                // Thick pen (size 12) loop and a highlighter over the dark header, for texture checks.
                typeof(EditorWindow).GetField("_penSize", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, 12.0);
                var thick = new List<(double, double)>();
                for (int i = 0; i <= 80; i++) { double t = i / 80.0 * Math.PI * 2; thick.Add((0.50 + 0.08 * Math.Cos(t) + 0.03 * Math.Cos(3 * t), 0.72 + 0.12 * Math.Sin(t))); }
                Drag(thick, steps: 1);
                Drag(new[] { (0.62, 0.62), (0.70, 0.90), (0.78, 0.62), (0.86, 0.90) }, steps: 6);
                Check("HighlighterToolBtn");
                Drag(new[] { (0.05, 0.06), (0.55, 0.06) }, steps: 30);
                Drag(new[] { (0.05, 0.36), (0.40, 0.36) }, steps: 30);
                Shot("pointer-pen-thick.png");
                RenderToPng(editor, Path.Combine(outDir, "pointer-pen-thick-200pct.png"), dpi: 192);


                // Text: click to open, type, commit by clicking away, then reselect.
                Check("TextToolBtn");
                Click(0.10, 0.92); Pump(4);
                var textBox = canvas.Children.OfType<TextBox>().Last();
                double mountW = textBox.Width, mountH = textBox.Height;
                textBox.Text = "Hello gjpq text"; Pump(3);
                Shot("pointer-text-editing.png");
                // Survey grows the box from measured content while typing and never shrinks it
                // below the font-derived mount height (TextEditOverlay.jsx:239-270).
                textBox.Text = "Hello gjpq text that wraps onto several rendered lines"; Pump(4);
                double grownH = textBox.Height;
                Shot("pointer-text-growing.png");
                textBox.Text = "Hi"; Pump(4);
                Click(0.02, 0.02); Pump(4);
                Shot("pointer-text-committed.png");
                // Committed NEW text hugs a single rendered line: outer width is
                // ceil(maxLineWidth + 2) + 2*pad, height is content + 2*pad (textEditCommit.js:137-148).
                var committed = canvas.Children.OfType<FrameworkElement>()
                    .Last(el => el.Tag is AnnotationData { Type: AnnotationData.TypeText });
                File.WriteAllText(Path.Combine(outDir, "text-geometry.txt"),
                    $"mount={mountW}x{mountH} grown={grownH} " +
                    $"committed={committed.Width}x{committed.Height} " +
                    $"selected={typeof(EditorWindow).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)}");

                // Survey re-opens an existing textbox on a plain (non-drag) text-tool click
                // (PDFViewer.jsx:31502-31519) instead of needing a double-click.
                Check("TextToolBtn");
                Click(0.10, 0.92); Pump(4);
                File.WriteAllText(Path.Combine(outDir, "text-reedit.txt"),
                    $"editors={canvas.Children.OfType<TextBox>().Count()} " +
                    $"text={canvas.Children.OfType<TextBox>().LastOrDefault()?.Text}");
                Shot("pointer-text-reedit.png");
                // Blanking a RE-EDIT keeps the box (Survey commits text:'' — textEditCommit.js:200).
                var reedit = canvas.Children.OfType<TextBox>().Last();
                reedit.Text = ""; Pump(3);
                Click(0.02, 0.02); Pump(4);
                File.WriteAllText(Path.Combine(outDir, "text-blank-reedit.txt"),
                    $"texts={canvas.Children.OfType<FrameworkElement>().Count(el => el.Tag is AnnotationData { Type: AnnotationData.TypeText })}");
                Shot("pointer-text-blanked.png");
                // A text drag sets the WRAP WIDTH ONLY; the dragged height is discarded.
                Check("TextToolBtn");
                editor.PointerDown(P(0.40, 0.90)); Pump(1);
                for (int i = 1; i <= 10; i++) { editor.PointerMove(P(0.40 + 0.20 * i / 10.0, 0.90 + 0.06 * i / 10.0)); Pump(1); }
                Shot("pointer-text-drag-preview.png");
                editor.PointerUp(P(0.60, 0.96)); Pump(4);
                var dragged = canvas.Children.OfType<TextBox>().Last();
                File.WriteAllText(Path.Combine(outDir, "text-drag.txt"),
                    $"box={dragged.Width}x{dragged.Height} singleLine={AnnotationFactory.SingleLineBoxHeight(dragged.FontSize)}");
                dragged.Text = "Dragged wrap width"; Pump(3);
                Click(0.02, 0.02); Pump(4);
                Shot("pointer-text-dragged.png");

                // Align grid: Survey's 26x26 cells with a centred 6px accent dot on real chrome.
                Check("TextToolBtn");
                Click(0.72, 0.90); Pump(4); // open an editor so the rich-text row is up
                ((Button)editor.FindName("AlignMenuBtn")!).RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(6);
                var alignGrid = (System.Windows.Controls.Primitives.UniformGrid)editor.FindName("AlignGrid")!;
                File.WriteAllText(Path.Combine(outDir, "align-grid.txt"),
                    $"grid={alignGrid.Width}x{alignGrid.Height} desired={alignGrid.DesiredSize} " +
                    $"cell={alignGrid.Children.OfType<Button>().First().ActualWidth}");
                ((System.Windows.Controls.Primitives.Popup)editor.FindName("AlignPopup")!).IsOpen = false; Pump(2);
                typeof(EditorWindow).GetMethod("CancelText", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, null);
                Pump(3);

                // Callout: a sub-MinDrag click is a cancel (Survey commits nothing).
                Check("CalloutToolBtn");
                int calloutsBeforeClick = canvas.Children.OfType<CalloutAnnotation>().Count();
                Click(0.30, 0.95); Pump(4);
                File.WriteAllText(Path.Combine(outDir, "callout-short-click.txt"),
                    $"before={calloutsBeforeClick} after={canvas.Children.OfType<CalloutAnnotation>().Count()} " +
                    $"editors={canvas.Children.OfType<TextBox>().Count()}");

                // Callout: drag tip→box (ghost mid-drag), type, commit, select.
                Check("CalloutToolBtn");
                editor.PointerDown(P(0.60, 0.95)); Pump(1);
                for (int i = 1; i <= 8; i++)
                {
                    double t = i / 8.0;
                    editor.PointerMove(P(0.60 + 0.25 * t, 0.95 - 0.07 * t));
                    Pump(1);
                }
                Shot("pointer-callout-ghost.png");
                editor.PointerUp(P(0.85, 0.88)); Pump(4);
                var calloutBox = canvas.Children.OfType<TextBox>().Last();
                calloutBox.Text = "Callout gjpq"; Pump(3);
                Shot("pointer-callout-editing.png");
                // Auto-grow: enough text to wrap past the drag-created height.
                var grownCallout = canvas.Children.OfType<CalloutAnnotation>().Last();
                double heightBeforeGrow = grownCallout.Layout.Box.Height;
                calloutBox.Text = "Callout gjpq wrapping onto several lines to grow the box"; Pump(4);
                Shot("pointer-callout-growing.png");
                File.WriteAllText(Path.Combine(outDir, "callout-grow.txt"),
                    $"before={heightBeforeGrow} after={grownCallout.Layout.Box.Height} " +
                    $"tool={typeof(EditorWindow).GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)}");
                calloutBox.Text = "Callout gjpq"; Pump(3);
                Click(0.02, 0.02); Pump(4);
                Check("SelectToolBtn");
                Click(0.85, 0.88); Shot("pointer-callout-selected.png");
                Click(0.10, 0.92); Shot("pointer-text-selected.png");

                // ---- TEXT FORMAT PARITY PROBE -------------------------------------
                {
                    var log = new System.Text.StringBuilder();
                    var selField = typeof(EditorWindow).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var activeField = typeof(EditorWindow).GetField("_activeText", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var boldBtn = (Button)editor.FindName("BoldBtn")!;
                    var underlineBtn = (Button)editor.FindName("UnderlineBtn")!;
                    var aaBtn = (Button)editor.FindName("TextEditBtn")!;
                    var strip = (FrameworkElement)editor.FindName("TextStylePanel")!;
                    var sizeInput = (TextBox)editor.FindName("SizeInput")!;
                    var famBox = (ComboBox)editor.FindName("FontFamilyBox")!;
                    var sizeBox = (ComboBox)editor.FindName("FontSizeBox")!;
                    void Fire(Button b) { b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(4); }
                    void Invoke(string name) => typeof(EditorWindow)
                        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                    void Hist(string name)
                    {
                        typeof(EditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(editor, null);
                        Pump(6);
                    }
                    void PickFamily(string family)
                    {
                        foreach (ComboBoxItem it in famBox.Items.OfType<ComboBoxItem>())
                            if (it.Tag as string == family) { famBox.SelectedItem = it; break; }
                        Pump(4);
                    }
                    void PickSize(double size)
                    {
                        foreach (ComboBoxItem it in sizeBox.Items.OfType<ComboBoxItem>())
                            if (it.Tag is double d && Math.Abs(d - size) < 0.01) { sizeBox.SelectedItem = it; break; }
                        Pump(4);
                    }
                    string Desc(object? o) => o is null ? "null" : o.GetType().Name;
                    TextBlock? TextOf(FrameworkElement? fe) => fe as TextBlock
                        ?? (fe as Border)?.Child as TextBlock
                        ?? (fe as Grid)?.Children.OfType<TextBlock>().FirstOrDefault();
                    FrameworkElement? Sel() => selField.GetValue(editor) as FrameworkElement;
                    TextBox? Live() => activeField.GetValue(editor) as TextBox;
                    FrameworkElement? LastText() => canvas.Children.OfType<FrameworkElement>()
                        .LastOrDefault(f => (f.Tag as AnnotationData)?.Type == AnnotationData.TypeText);

                    // TXT-1: the Aa button reads the annotation TYPE, so a committed plain
                    // text (a Grid, never a bare TextBlock) lights it up and re-opens.
                    var selText = Sel();
                    var textMeta = selText?.Tag as AnnotationData;
                    log.AppendLine($"[TEXT] selectedVisual={Desc(selText)} metaType={textMeta?.Type}");
                    log.AppendLine($"[TEXT] stripVisible={strip.Visibility} AaEnabled={aaBtn.IsEnabled}");
                    Fire(boldBtn);
                    log.AppendLine($"[TEXT] afterBold(selected) meta.Bold={(Sel()?.Tag as AnnotationData)?.Bold} weight={TextOf(Sel())?.FontWeight}");
                    Fire(aaBtn);
                    log.AppendLine($"[TEXT] afterAa activeEditor={(Live() is null ? "null" : "open")} stripVisible={strip.Visibility} editors={canvas.Children.OfType<TextBox>().Count()}");

                    // TXT-7: a size change on the OPEN editor re-measures immediately, with
                    // no keystroke in between.
                    double hBeforeSize = Live()?.Height ?? 0;
                    PickSize(36);
                    log.AppendLine($"[TEXT] afterSize36(open) editorSize={Live()?.FontSize} heightBefore={hBeforeSize:F1} heightAfter={Live()?.Height:F1}");
                    Fire(underlineBtn);
                    log.AppendLine($"[TEXT] afterUnderline(open) decorations={(Live()?.TextDecorations?.Count ?? 0)}");
                    Invoke("CommitText"); Pump(4);
                    var commitTarget = LastText();
                    var cm = commitTarget?.Tag as AnnotationData;
                    log.AppendLine($"[TEXT] afterCommit bold={cm?.Bold} underline={cm?.Underline} fontSize={cm?.FontSize} labelDecorations={(TextOf(commitTarget)?.TextDecorations?.Count ?? 0)}");

                    // TXT-5: re-opening that text must reload ITS style (underline included)
                    // rather than the last-formatted object's, and commit it back unchanged.
                    Check("SelectToolBtn");
                    selField.SetValue(editor, commitTarget);
                    Fire(aaBtn);
                    log.AppendLine($"[TEXT] reEdit editorDecorations={(Live()?.TextDecorations?.Count ?? 0)} editorWeight={Live()?.FontWeight} editorSize={Live()?.FontSize} editorFamily={Live()?.FontFamily}");
                    Invoke("CommitText"); Pump(4);
                    var roundTrip = LastText()?.Tag as AnnotationData;
                    log.AppendLine($"[TEXT] reEditCommit bold={roundTrip?.Bold} underline={roundTrip?.Underline} fontSize={roundTrip?.FontSize} family={roundTrip?.FontFamily}");

                    // TXT-6: the Size box is the STROKE control for a selected text box.
                    var sizeTarget = LastText();
                    selField.SetValue(editor, sizeTarget);
                    Invoke("UpdateContextPanels"); Pump(2);
                    log.AppendLine($"[TEXT] sizeBoxReadBack={sizeInput.Text} (meta.Thickness={(sizeTarget?.Tag as AnnotationData)?.Thickness})");
                    sizeInput.Text = "6";
                    Invoke("CommitSizeInput"); Pump(4);
                    log.AppendLine($"[TEXT] afterSize6 meta.Thickness={(Sel()?.Tag as AnnotationData)?.Thickness}");

                    // ---- CALLOUT ----
                    Check("SelectToolBtn");
                    Click(0.85, 0.88); Pump(3);
                    var selCallout = Sel() as CalloutAnnotation;
                    var beforeMeta = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] selected={Desc(selCallout)} AaEnabled={aaBtn.IsEnabled} stripVisible={strip.Visibility}");
                    log.AppendLine($"[CALLOUT] before bold={beforeMeta?.Bold} family={beforeMeta?.FontFamily} size={beforeMeta?.FontSize}");
                    // TXT-2: the rich-text strip has to come up for the callout editor too.
                    Fire(aaBtn);
                    log.AppendLine($"[CALLOUT] afterAa activeEditor={(Live() is null ? "null" : "open")} stripVisible={strip.Visibility}");
                    // TXT-5: the strip reloads the callout's own style, not the text box's.
                    log.AppendLine($"[CALLOUT] seeded editorWeight={Live()?.FontWeight} editorSize={Live()?.FontSize} editorFamily={Live()?.FontFamily}");
                    // TXT-3 + TXT-8: bold writes through to meta and label AT CLICK TIME.
                    Fire(boldBtn);
                    log.AppendLine($"[CALLOUT] afterBold(open) editorWeight={Live()?.FontWeight} meta.Bold={(selCallout?.Tag as AnnotationData)?.Bold} labelWeight={selCallout?.Label.FontWeight} labelVisible={selCallout?.Label.Visibility}");
                    // TXT-4: family + size are not excluded for callouts any more. Type first,
                    // so the style click has to render the LIVE text (Survey's overlay owns it)
                    // and re-measure the box under the new metrics on the same click instead of
                    // reverting to the pre-edit string at the pre-edit height.
                    if (Live() is TextBox ctb) { ctb.Text = "Callout gjpq wraps onto more lines"; Pump(4); }
                    double cBoxBefore = selCallout?.Layout.Box.Height ?? 0;
                    PickFamily("Courier New");
                    PickSize(36);
                    var cMeta = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] liveText label={selCallout?.Label.Text} editor={Live()?.Text}");
                    log.AppendLine($"[CALLOUT] regrow boxBefore={cBoxBefore:F1} boxAfter={selCallout?.Layout.Box.Height:F1} editorH={Live()?.Height:F1}");
                    log.AppendLine($"[CALLOUT] afterFamily/Size(open) editorFamily={Live()?.FontFamily} editorSize={Live()?.FontSize} labelFamily={selCallout?.Label.FontFamily} labelSize={selCallout?.Label.FontSize} meta.Family={cMeta?.FontFamily} meta.Size={cMeta?.FontSize}");
                    Shot("pointer-callout-format-open.png");
                    Invoke("CommitCalloutText"); Pump(4);
                    var afterMeta = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] afterCommit bold={afterMeta?.Bold} family={afterMeta?.FontFamily} size={afterMeta?.FontSize} text={afterMeta?.Text} labelWeight={selCallout?.Label.FontWeight} labelFamily={selCallout?.Label.FontFamily} labelSize={selCallout?.Label.FontSize} labelVisible={selCallout?.Label.Visibility}");
                    // TXT-8: each strip click left its own undo checkpoint.
                    Hist("UndoAsync");
                    var undone1 = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] undo1 size={undone1?.FontSize} family={undone1?.FontFamily} bold={undone1?.Bold}");
                    Hist("UndoAsync");
                    var undone2 = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] undo2 size={undone2?.FontSize} family={undone2?.FontFamily} bold={undone2?.Bold}");
                    Hist("RedoAsync"); Hist("RedoAsync");
                    var redone = selCallout?.Tag as AnnotationData;
                    log.AppendLine($"[CALLOUT] redo size={redone?.FontSize} family={redone?.FontFamily} bold={redone?.Bold}");
                    File.WriteAllText(Path.Combine(outDir, "text-format-audit.txt"), log.ToString());
                    Shot("pointer-text-format-audit.png");
                    // The two restyled objects up close: the bold + underlined 36pt text box
                    // that survived a re-edit round trip, and the bold Courier New 36 callout.
                    void CloseUp(FrameworkElement? fe, string name)
                    {
                        if (fe is null || fe.ActualWidth < 1 || fe.ActualHeight < 1) return;
                        RenderElementToPng(fe, Path.Combine(outDir, name));
                    }
                    CloseUp(selCallout?.Label, "textfmt-committed-callout.png");

                    // ---- ROUND 2 PROBES: colour-channel separation + edit-mode selection ----
                    var r2 = new System.Text.StringBuilder();
                    // R2-A: does the top-row STROKE swatch recolour a selected text box's GLYPHS?
                    // Survey patches only `stroke` (the border rect); `fill` (the glyphs) is a
                    // separate field changed only by the rich-text font-colour swatch.
                    Check("SelectToolBtn");
                    var t1 = LastText();
                    selField.SetValue(editor, t1);
                    Invoke("UpdateSelectionVisual"); Pump(2);
                    var beforeText = t1?.Tag as AnnotationData;
                    var glyph0 = (TextOf(t1)?.Foreground as SolidColorBrush)?.Color;
                    typeof(EditorWindow).GetMethod("RestyleSelected", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object?[] { Color.FromRgb(0x11, 0xEE, 0x22), null, null, null, null });
                    Pump(4);
                    var afterText = LastText()?.Tag as AnnotationData;
                    var chrome0 = (LastText() as Grid)?.Children.OfType<System.Windows.Shapes.Shape>().FirstOrDefault();
                    r2.AppendLine($"[R2-A strokeSwatch->text] glyphBefore={glyph0} glyphAfter={(TextOf(LastText())?.Foreground as SolidColorBrush)?.Color} " +
                        $"borderAfter={(chrome0?.Stroke as SolidColorBrush)?.Color} meta.Color={afterText?.Color} meta.FontColor={afterText?.FontColor ?? "null"}");

                    // R2-B: does the rich-text FONT COLOUR also repaint the border?
                    Fire(aaBtn); Pump(3);
                    typeof(EditorWindow).GetMethod("SetFontColor", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object?[] { Color.FromRgb(0xFF, 0x00, 0xFF) });
                    Pump(3);
                    Invoke("CommitText"); Pump(4);
                    var fcMeta = LastText()?.Tag as AnnotationData;
                    var chrome1 = (LastText() as Grid)?.Children.OfType<System.Windows.Shapes.Shape>().FirstOrDefault();
                    r2.AppendLine($"[R2-B fontColour->text] meta.Color={fcMeta?.Color} meta.FontColor={fcMeta?.FontColor ?? "null"} " +
                        $"glyph={(TextOf(LastText())?.Foreground as SolidColorBrush)?.Color} border={(chrome1?.Stroke as SolidColorBrush)?.Color}");

                    // R2-C: is the annotation still SELECTED while its editor is open? Survey's
                    // handleEnterTextEditFromStrip leaves selectedToolbarAnnotation intact, so the
                    // top-row swatches keep patching the object during the edit.
                    var t2 = LastText();
                    selField.SetValue(editor, t2);
                    Invoke("UpdateSelectionVisual"); Pump(2);
                    Fire(aaBtn); Pump(3);
                    r2.AppendLine($"[R2-C editModeSelection] activeEditor={(Live() is null ? "null" : "open")} selected={Desc(Sel())}");
                    var midEdit = t2?.Tag as AnnotationData;
                    typeof(EditorWindow).GetMethod("RestyleSelected", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object?[] { Color.FromRgb(0x00, 0x00, 0xFF), 7.0, null, null, null });
                    Pump(3);
                    r2.AppendLine($"[R2-C midEditRestyle] before color={midEdit?.Color} thickness={midEdit?.Thickness} " +
                        $"after color={(t2?.Tag as AnnotationData)?.Color} thickness={(t2?.Tag as AnnotationData)?.Thickness}");
                    Invoke("CommitText"); Pump(4);

                    // R2-D: multi-select restyle — two texts with different sizes, then Bold.
                    // Survey has no multi-object text restyle; WinShot stamps every _text* global
                    // (seeded from ONE element) onto every selected text.
                    Check("TextToolBtn");
                    Click(0.30, 0.72); Pump(3);
                    if (Live() is TextBox ta) { ta.Text = "AAA"; Pump(3); }
                    PickSize(12); Invoke("CommitText"); Pump(4);
                    var multiA = LastText();
                    Check("TextToolBtn");
                    Click(0.55, 0.72); Pump(3);
                    if (Live() is TextBox tbb) { tbb.Text = "BBB"; Pump(3); }
                    PickSize(48); Invoke("CommitText"); Pump(4);
                    var multiB = LastText();
                    Check("SelectToolBtn");
                    var selListField = typeof(EditorWindow).GetField("_selection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var selList = (System.Collections.IList)selListField.GetValue(editor)!;
                    selList.Clear();
                    if (multiA is not null) selList.Add(multiA);
                    if (multiB is not null) selList.Add(multiB);
                    selField.SetValue(editor, multiA);
                    Invoke("SyncChromeFromSelection"); Invoke("UpdateContextPanels"); Pump(3);
                    r2.AppendLine($"[R2-D multiSelect] before A.size={(multiA?.Tag as AnnotationData)?.FontSize} B.size={(multiB?.Tag as AnnotationData)?.FontSize}");
                    Fire(boldBtn); Pump(4);
                    var mA = canvas.Children.OfType<FrameworkElement>().FirstOrDefault(f => ReferenceEquals(f, multiA))?.Tag as AnnotationData;
                    var afterList = (System.Collections.IList)selListField.GetValue(editor)!;
                    var mA2 = (afterList.Count > 0 ? afterList[0] as FrameworkElement : null)?.Tag as AnnotationData;
                    var mB2 = (afterList.Count > 1 ? afterList[1] as FrameworkElement : null)?.Tag as AnnotationData;
                    r2.AppendLine($"[R2-D multiSelect] after A.size={mA2?.FontSize} A.bold={mA2?.Bold} B.size={mB2?.FontSize} B.bold={mB2?.Bold} (mA={mA?.FontSize})");

                    // R2-E: rotated text box → Aa → commit. Survey's existing-text commit clones
                    // the original (angle included) and compensates left/top for the height delta
                    // (textEditCommit.js:190-210). WinShot's CommitText builds a NEW label with
                    // only Canvas.Left/Top, so the angle and the drag offset are dropped.
                    selList.Clear();
                    var rot = multiB;
                    if (rot is not null)
                    {
                        selList.Add(rot);
                        selField.SetValue(editor, rot);
                        AnnotationTransform.Apply(rot, new Vector(40, 25), 30);
                        Pump(3);
                        r2.AppendLine($"[R2-E rotatedReEdit] before angle={AnnotationTransform.AngleOf(rot):F1} " +
                            $"offset={AnnotationTransform.OffsetOf(rot)} left={Canvas.GetLeft(rot):F1} top={Canvas.GetTop(rot):F1} " +
                            $"transform={rot.RenderTransform.GetType().Name}");
                        Fire(aaBtn); Pump(3);
                        r2.AppendLine($"[R2-E rotatedReEdit] editorOpen={(Live() is null ? "null" : "open")} " +
                            $"editorLeft={(Live() is null ? -1 : Canvas.GetLeft(Live()!)):F1} editorTop={(Live() is null ? -1 : Canvas.GetTop(Live()!)):F1}");
                        Invoke("CommitText"); Pump(4);
                        var rotAfter = LastText();
                        r2.AppendLine($"[R2-E rotatedReEdit] after angle={(rotAfter is null ? -1 : AnnotationTransform.AngleOf(rotAfter)):F1} " +
                            $"offset={(rotAfter is null ? default : AnnotationTransform.OffsetOf(rotAfter))} " +
                            $"left={(rotAfter is null ? -1 : Canvas.GetLeft(rotAfter)):F1} top={(rotAfter is null ? -1 : Canvas.GetTop(rotAfter)):F1} " +
                            $"transform={rotAfter?.RenderTransform.GetType().Name}");
                    }

                    // R2-F: one align-cell click -> how many undo entries? Survey issues two
                    // single-key patches (AppShell.jsx:1974-1975); OnAlignCellClick's two
                    // SelectComboByTag calls each re-enter RestyleSelectedTextFormat.
                    var undoField = typeof(EditorWindow).GetField("_undoStack", BindingFlags.Instance | BindingFlags.NonPublic);
                    var alignTarget = LastText();
                    if (alignTarget is not null && undoField is not null)
                    {
                        selList.Clear(); selList.Add(alignTarget);
                        selField.SetValue(editor, alignTarget);
                        Invoke("SyncChromeFromSelection"); Pump(2);
                        int depth0 = ((System.Collections.ICollection)undoField.GetValue(editor)!).Count;
                        var cell = (editor.FindName("AlignGrid") as Panel)?.Children.OfType<Button>()
                            .FirstOrDefault(b => (b.Tag as string) == "center|middle");
                        if (cell is not null) Fire(cell);
                        int depth1 = ((System.Collections.ICollection)undoField.GetValue(editor)!).Count;
                        r2.AppendLine($"[R2-F alignCell] undoDepth {depth0} -> {depth1} (delta={depth1 - depth0})");
                    }
                    File.WriteAllText(Path.Combine(outDir, "text-format-round2.txt"), r2.ToString());
                    Shot("pointer-text-format-round2.png");
                }
                // ---- END TEXT FORMAT PARITY PROBE ---------------------------------
                // --- Effects (WinShot-only; Survey has no equivalent): pixelate and blur. ---
                EditorWindow.PointerArgs PA(Point c, int clicks = 1) =>
                    new EditorWindow.PointerArgs(c, canvas.TranslatePoint(c, viewport), MouseButton.Left, clicks);
                void DragAbs(Point a, Point b, int steps = 12)
                {
                    editor.PointerDown(PA(a)); Pump(1);
                    for (int i = 1; i <= steps; i++) { editor.PointerMove(PA(a + (b - a) * (i / (double)steps))); Pump(1); }
                    editor.PointerUp(PA(b)); Pump(3);
                }
                Check("PixelateToolBtn"); Drag(new[] { (0.62, 0.30), (0.80, 0.45) });
                Check("BlurToolBtn"); Drag(new[] { (0.62, 0.50), (0.80, 0.62) }); Pump(30);
                Shot("pointer-effects.png");

                // --- Callout mechanics: knee and tip move independently, corners resize, box moves. ---
                Check("SelectToolBtn");
                Click(0.85, 0.88);
                var calloutEl = canvas.Children.OfType<CalloutAnnotation>().Last();
                Point CalloutPt(Func<CalloutLayout, Point> pick) => pick(calloutEl.Layout) + AnnotationTransform.OffsetOf(calloutEl);
                Point knee = CalloutPt(l => l.Knee);
                DragAbs(knee, knee + new Vector(0.05 * W, 0.06 * H)); Shot("pointer-callout-knee.png");
                Point tip = CalloutPt(l => l.Tip);
                DragAbs(tip, tip + new Vector(-0.08 * W, 0.03 * H)); Shot("pointer-callout-tip.png");
                Point br = CalloutPt(l => l.Box.BottomRight);
                var boxBefore = calloutEl.Layout.Box;
                var hitField = typeof(EditorWindow).GetMethod("HitTestHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
                int hitIndex = (int)hitField.Invoke(editor, new object[] { br })!;
                DragAbs(br, br + new Vector(0.05 * W, 0.03 * H)); Shot("pointer-callout-resize.png");
                File.WriteAllText(Path.Combine(outDir, "callout-resize.txt"),
                    $"hit={hitIndex} br={br} before={boxBefore} after={calloutEl.Layout.Box} offset={AnnotationTransform.OffsetOf(calloutEl)} W={W} H={H}");
                Point boxCentre = CalloutPt(l => new Point(l.Box.X + l.Box.Width / 2, l.Box.Y + l.Box.Height / 2));
                DragAbs(boxCentre, boxCentre + new Vector(-0.06 * W, -0.10 * H)); Shot("pointer-callout-moved.png");

                // Invalid drop: the knee dragged into the text box is rejected on release and
                // the WHOLE callout snaps back to its pre-drag state (Survey dropSafe rollback).
                var beforeDrop = calloutEl.Layout;
                Point kneeNow = CalloutPt(l => l.Knee);
                Point insideBox = CalloutPt(l => new Point(l.Box.X + l.Box.Width / 2, l.Box.Y + l.Box.Height / 2));
                editor.PointerDown(PA(kneeNow)); Pump(1);
                for (int i = 1; i <= 10; i++) { editor.PointerMove(PA(kneeNow + (insideBox - kneeNow) * (i / 10.0))); Pump(1); }
                Shot("pointer-callout-invalid-drag.png");
                editor.PointerUp(PA(insideBox)); Pump(3);
                Shot("pointer-callout-invalid-drop.png");
                File.WriteAllText(Path.Combine(outDir, "callout-invalid-drop.txt"),
                    $"knee before={beforeDrop.Knee} after={calloutEl.Layout.Knee} " +
                    $"tip before={beforeDrop.Tip} after={calloutEl.Layout.Tip} " +
                    $"box before={beforeDrop.Box} after={calloutEl.Layout.Box}");
                Click(0.02, 0.02);

                // Hover: Survey paints the blue glow (not handles) on an unselected callout.
                editor.PointerMove(PA(CalloutPt(l => new Point(l.Box.X + l.Box.Width / 2, l.Box.Y + l.Box.Height / 2))));
                Pump(3); Shot("pointer-callout-hover.png");
                editor.PointerMove(PA(new Point(0.02 * W, 0.02 * H))); Pump(3);

                // --- Rotation: hover the grip (pill), drag it (live angle), release. ---
                Click(0.10, 0.40);
                Point grip = (Point)typeof(EditorWindow).GetField("_gripContent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                // Survey opens the pill only after 150 ms of hover intent, so dwell on the grip.
                editor.PointerMove(PA(grip)); Pump(3); Shot("pointer-rotation-hover-early.png");
                editor.PointerMove(PA(grip)); Pump(14); Shot("pointer-rotation-hover.png");
                editor.PointerDown(PA(grip)); Pump(1);
                var rectCentre = new Point(0.23 * W, 0.385 * H);
                double radius = (grip - rectCentre).Length;
                Point last = grip;
                for (int i = 1; i <= 12; i++)
                {
                    double ang = -Math.PI / 2 + i / 12.0 * (Math.PI / 5);
                    last = new Point(rectCentre.X + radius * Math.Cos(ang), rectCentre.Y + radius * Math.Sin(ang));
                    editor.PointerMove(PA(last)); Pump(1);
                }
                Shot("pointer-rotation-dragging.png");
                editor.PointerUp(PA(last)); Pump(3); Shot("pointer-rotated.png");
                var rotatedRect = canvas.Children.OfType<FrameworkElement>()
                    .First(f => f.Tag is AnnotationData { Type: AnnotationData.TypeRectangle });
                File.WriteAllText(Path.Combine(outDir, "rotation-angle.txt"),
                    $"angle={AnnotationTransform.AngleOf(rotatedRect)} grip={grip} assumedCentre={rectCentre} last={last}");
                Click(0.02, 0.02);

                // --- RESIZE PARITY: a rotated mark must resize against a PINNED world anchor,
                // ink must thicken with the box, and a rotated line must keep its tilt. ---
                {
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear();
                    Pump(4);
                    var log = new System.Text.StringBuilder();
                    var selField = typeof(EditorWindow).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    FrameworkElement Sel() => (FrameworkElement)selField.GetValue(editor)!;
                    Rect Unrot(FrameworkElement f) => (Rect)typeof(EditorWindow)
                        .GetMethod("UnrotatedCanvasBounds", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new object[] { f })!;
                    // The world position of a box corner, i.e. what the eye actually sees.
                    Point WorldCorner(FrameworkElement f, Func<Rect, Point> pick)
                    {
                        Rect b = Unrot(f);
                        return AnnotationRotation.RotatePoint(pick(b),
                            new Point(b.Left + b.Width / 2, b.Top + b.Height / 2),
                            AnnotationTransform.AngleOf(f));
                    }
                    void Rotate(FrameworkElement f, double deg)
                    {
                        AnnotationTransform.SetAngle(f, deg);
                        if (f.Tag is AnnotationData m) m.Angle = deg;
                        InvokePrivate(editor, "UpdateSelectionVisual");
                        Pump(3);
                    }

                    // RESIZE-01: rect rotated 30 degrees, TL dragged. BR must not move.
                    Check("RectangleToolBtn");
                    Drag(new[] { (0.20, 0.30), (0.50, 0.60) });
                    Check("SelectToolBtn");
                    Click(0.20, 0.45);
                    var rotRect = Sel();
                    Rotate(rotRect, 30);
                    Shot("resize-rotated-rect-before.png");
                    Point brBefore = WorldCorner(rotRect, r => r.BottomRight);
                    Point tlGrab = WorldCorner(rotRect, r => r.TopLeft);
                    int tlHandle = (int)typeof(EditorWindow)
                        .GetMethod("HitTestHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, new object[] { tlGrab })!;
                    var brTrack = new List<Point>();
                    editor.PointerDown(PA(tlGrab)); Pump(1);
                    var pull = tlGrab + new Vector(-0.06 * W, -0.05 * H);
                    for (int i = 1; i <= 10; i++)
                    {
                        editor.PointerMove(PA(tlGrab + (pull - tlGrab) * (i / 10.0))); Pump(1);
                        brTrack.Add(WorldCorner(rotRect, r => r.BottomRight));
                    }
                    Shot("resize-rotated-rect-dragging.png");
                    editor.PointerUp(PA(pull)); Pump(3);
                    Shot("resize-rotated-rect-after.png");
                    double brDrift = brTrack.Max(p => (p - brBefore).Length);
                    log.AppendLine($"[R1] rotated rect TL drag: handle={tlHandle} angle={AnnotationTransform.AngleOf(rotRect)} " +
                        $"anchorBR before={brBefore:F2} after={WorldCorner(rotRect, r => r.BottomRight):F2} maxDrift={brDrift:F3}");

                    // RESIZE-03/05: ink thickens with the box, and a drag past the anchor mirrors.
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                    typeof(EditorWindow).GetField("_penSize", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(editor, 6.0);
                    Check("FreehandToolBtn");
                    Drag(new[] { (0.20, 0.30), (0.30, 0.24), (0.40, 0.36), (0.50, 0.30) }, steps: 4);
                    Check("SelectToolBtn");
                    var inkEl = canvas.Children.OfType<System.Windows.Shapes.Path>()
                        .Last(q => q.Tag is AnnotationData { Type: AnnotationData.TypeFreehand });
                    selField.SetValue(editor, inkEl);
                    InvokePrivate(editor, "UpdateSelectionVisual"); Pump(3);
                    Rect inkBefore = Unrot(inkEl);
                    double inkAreaBefore = inkEl.Data!.GetArea();
                    Shot("resize-ink-before.png");
                    Point inkBr = new(inkBefore.Right, inkBefore.Bottom);
                    DragAbs(inkBr, new Point(inkBefore.Left + inkBefore.Width * 3, inkBefore.Top + inkBefore.Height * 3), 12);
                    Shot("resize-ink-grown-3x.png");
                    var inkMeta = (AnnotationData)inkEl.Tag!;
                    log.AppendLine($"[R3] ink 3x: thickness 6 -> {inkMeta.Thickness:F2} " +
                        $"bounds {inkBefore.Width:F1}x{inkBefore.Height:F1} -> {Unrot(inkEl).Width:F1}x{Unrot(inkEl).Height:F1} " +
                        $"area {inkAreaBefore:F0} -> {inkEl.Data!.GetArea():F0}");
                    // Now pull the LEFT edge past the right edge: Survey mirrors the stroke.
                    Rect grown = Unrot(inkEl);
                    Point leftMid = new(grown.Left, grown.Top + grown.Height / 2);
                    DragAbs(leftMid, new Point(grown.Right + grown.Width * 0.5, leftMid.Y), 14);
                    Shot("resize-ink-mirrored.png");
                    log.AppendLine($"[R5] ink left handle dragged past the right edge: bounds={Unrot(inkEl)}");

                    // RESIZE-02: a rotated line keeps its tilt while an endpoint is dragged.
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                    Check("LineToolBtn");
                    Drag(new[] { (0.25, 0.40), (0.55, 0.40) });
                    Check("SelectToolBtn");
                    Click(0.40, 0.40);
                    var rotLine = Sel();
                    Rotate(rotLine, 30);
                    Shot("resize-rotated-line-before.png");
                    var linePts = ((AnnotationData)rotLine.Tag!).Points!;
                    Point lineFromLocal = new(linePts[0][0], linePts[0][1]);
                    Rect lb = Unrot(rotLine);
                    Point linePivot = new(lb.Left + lb.Width / 2, lb.Top + lb.Height / 2);
                    Point fromWorld = AnnotationRotation.RotatePoint(lineFromLocal, linePivot, 30);
                    DragAbs(fromWorld, fromWorld + new Vector(-0.07 * W, 0.05 * H), 12);
                    Shot("resize-rotated-line-after.png");
                    log.AppendLine($"[R2] rotated line endpoint drag: angle 30 -> {AnnotationTransform.AngleOf(rotLine)}");

                    // RESIZE-08: a rotated line in bbox edit mode scales against a PINNED world
                    // anchor, exactly like the rotated rect in R1 — the corner you pull against
                    // must stay on the same pixel for the whole drag.
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                    Check("LineToolBtn");
                    Drag(new[] { (0.25, 0.35), (0.60, 0.55) });
                    Check("SelectToolBtn");
                    Click(0.42, 0.45); Pump(3);
                    var boxLine = Sel();
                    var bboxTarget = typeof(EditorWindow)
                        .GetField("_bboxEditTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    bboxTarget.SetValue(editor, boxLine);   // Survey's double-click bbox edit mode
                    Rotate(boxLine, 30);
                    Shot("resize-rotated-line-bbox-before.png");
                    Point lineBl = WorldCorner(boxLine, r => r.BottomLeft);
                    Point lineTr = WorldCorner(boxLine, r => r.TopRight);
                    var blTrack = new List<Point>();
                    editor.PointerDown(PA(lineTr)); Pump(1);
                    var linePull = lineTr + new Vector(0.06 * W, -0.05 * H);
                    for (int i = 1; i <= 10; i++)
                    {
                        editor.PointerMove(PA(lineTr + (linePull - lineTr) * (i / 10.0))); Pump(1);
                        blTrack.Add(WorldCorner(boxLine, r => r.BottomLeft));
                    }
                    Shot("resize-rotated-line-bbox-dragging.png");
                    editor.PointerUp(PA(linePull)); Pump(3);
                    Shot("resize-rotated-line-bbox-after.png");
                    log.AppendLine($"[R8] rotated line bbox TR drag: angle 30 -> {AnnotationTransform.AngleOf(boxLine):F2} " +
                        $"anchorBL before={lineBl:F2} after={WorldCorner(boxLine, r => r.BottomLeft):F2} " +
                        $"maxDrift={blTrack.Max(p => (p - lineBl).Length):F3}");
                    bboxTarget.SetValue(editor, null);

                    // RESIZE-09/10: rect is border-flush (no dashed frame, padding 0); an
                    // ellipse keeps the frame with the handles centred on it.
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                    Check("RectangleToolBtn"); Drag(new[] { (0.10, 0.20), (0.40, 0.50) });
                    Check("EllipseToolBtn"); Drag(new[] { (0.55, 0.20), (0.85, 0.50) });
                    Check("SelectToolBtn");
                    var selRect = (FrameworkElement)editor.FindName("SelectionRect")!;
                    Click(0.10, 0.35); Pump(3);
                    Shot("resize-chrome-rect-borderflush.png");
                    log.AppendLine($"[R10] rect selected: dashedFrame={selRect.Visibility}");
                    Click(0.55, 0.35); Pump(3);
                    Shot("resize-chrome-ellipse-padded.png");
                    log.AppendLine($"[R10] ellipse selected: dashedFrame={selRect.Visibility} " +
                        $"frame={new Rect(Canvas.GetLeft(selRect), Canvas.GetTop(selRect), selRect.Width, selRect.Height)}");

                    // RESIZE-11: the top pill must NOT grab a click a pill-height above itself.
                    var pills = ((Canvas)editor.FindName("HandleLayer")!).Children
                        .OfType<System.Windows.Shapes.Rectangle>()
                        .Where(t => t.Visibility == Visibility.Visible && Math.Abs(t.Width - t.Height) > 1e-6)
                        .ToList();
                    if (pills.Count > 0)
                    {
                        var pill = pills[0];
                        var pc = new Point(Canvas.GetLeft(pill) + pill.Width / 2, Canvas.GetTop(pill) + pill.Height / 2);
                        int onPill = (int)typeof(EditorWindow)
                            .GetMethod("HitTestHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(editor, new object[] { pc })!;
                        int wellAbove = (int)typeof(EditorWindow)
                            .GetMethod("HitTestHandle", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(editor, new object[] { pc - new Vector(0, pill.Width / 2 - 1) })!;
                        log.AppendLine($"[R11] pill {pill.Width:F1}x{pill.Height:F1}: onCentre={onPill} " +
                            $"at {pill.Width / 2 - 1:F1}px perpendicular={wellAbove} (must be -1)");
                    }
                    // RESIZE-15: a counter in bbox edit mode gets the CANONICAL nub-up frame
                    // (2r x 2.5r) turned by pointerAngle+90 about the BUBBLE centre, so the
                    // grip lands in front of the tip instead of above an axis-aligned square.
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                    counterBtn.IsChecked = true; Pump(3);
                    Click(0.45, 0.40);
                    Check("SelectToolBtn");
                    Click(0.45, 0.40, clicks: 2); Pump(4);   // double-click enters bbox mode
                    Shot("resize-counter-bbox-frame.png");
                    var bboxField = typeof(EditorWindow).GetField("_bboxEditTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var counterSel = Sel();
                    log.AppendLine($"[R15] counter bbox mode: target={(bboxField.GetValue(editor) is null ? "null" : "set")} " +
                        $"badge={Unrot(counterSel).Width:F1}x{Unrot(counterSel).Height:F1} " +
                        $"pointerAngle={((AnnotationData)counterSel.Tag!).PointerAngle}");

                    File.WriteAllText(Path.Combine(outDir, "resize-parity.txt"), log.ToString());
                    InvokePrivate(editor, "Select", null);
                    canvas.Children.Clear(); Pump(4);
                }

                // --- Marquee: left→right = window (blue), right→left = crossing (green dashed). ---
                Check("SelectToolBtn"); Click(0.02, 0.02);
                void MarqueeDrag(double x0, double y0, double x1, double y1, string midShot, string endShot)
                {
                    editor.PointerDown(P(x0, y0)); Pump(1);
                    for (int i = 1; i <= 8; i++) { double t = i / 8.0; editor.PointerMove(P(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t)); Pump(1); }
                    Shot(midShot);
                    editor.PointerUp(P(x1, y1)); Pump(3); Shot(endShot);
                }
                MarqueeDrag(0.05, 0.20, 0.40, 0.56, "pointer-marquee-window-drag.png", "pointer-marquee-window.png");
                Click(0.02, 0.02);
                MarqueeDrag(0.75, 0.56, 0.50, 0.40, "pointer-marquee-crossing-drag.png", "pointer-marquee-crossing.png");
                Click(0.02, 0.02);

                // --- Undo / redo through the editor's own history. ---
                void History(string method, int times)
                {
                    var m = typeof(EditorWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
                    for (int k = 0; k < times; k++)
                    {
                        var task = (Task)m.Invoke(editor, null)!;
                        for (int i = 0; i < 100 && !task.IsCompleted; i++) Pump(2);
                        Pump(3);
                    }
                }
                History("UndoAsync", 2); Shot("pointer-undo-2.png");
                History("RedoAsync", 2); Shot("pointer-redo-2.png");

                // --- Crop: enter, drag a region, adjust a corner, apply, undo. ---
                ((Button)editor.FindName("CropUtilityBtn")!).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(4);
                Drag(new[] { (0.15, 0.20), (0.70, 0.70) }); Shot("pointer-crop-drag.png");
                DragAbs(new Point(0.70 * W, 0.70 * H), new Point(0.76 * W, 0.76 * H)); Shot("pointer-crop-adjusted.png");
                var cropPanel = (StackPanel)editor.FindName("CropPanel")!;
                var applyCrop = cropPanel.Children.OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Apply crop");
                applyCrop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump(40); Shot("pointer-crop-applied.png");
                History("UndoAsync", 1); Pump(20); Shot("pointer-crop-undone.png");

                Check("SpotlightToolBtn"); Drag(new[] { (0.05, 0.05), (0.40, 0.30) }); Pump(30);
                Shot("pointer-spotlight.png");

                // --- Ink parity (Survey one-to-one). Clean canvas so seams are readable. ---
                Check("SelectToolBtn");
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);

                // Constant-width pen at 3 / 12 / 30: a tight zig-zag (joins) and a
                // self-crossing X (the union must stay solid, no even-odd hole).
                var penSizeField = typeof(EditorWindow).GetField("_penSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Check("FreehandToolBtn");
                double[] penWidths = { 3, 12, 30 };
                for (int wi = 0; wi < penWidths.Length; wi++)
                {
                    penSizeField.SetValue(editor, penWidths[wi]);
                    double y0 = 0.10 + wi * 0.28;
                    Drag(new[] { (0.06, y0), (0.12, y0 + 0.10), (0.18, y0), (0.24, y0 + 0.10), (0.30, y0) }, steps: 4);
                    Drag(new[] { (0.36, y0), (0.48, y0 + 0.12), (0.48, y0), (0.36, y0 + 0.12) }, steps: 6);
                }
                Shot("pointer-ink-pen-widths.png");
                RenderToPng(editor, Path.Combine(outDir, "pointer-ink-pen-widths-200pct.png"), dpi: 192);

                // Highlighter: fixed rgba(255,193,7,.3) at max(sharedWidth, 8), multiply,
                // and a self-crossing loop that must not punch a hole.
                penSizeField.SetValue(editor, 3.0);
                Check("HighlighterToolBtn");
                Drag(new[] { (0.55, 0.10), (0.92, 0.10) }, steps: 20);
                Drag(new[] { (0.55, 0.22), (0.92, 0.34), (0.92, 0.22), (0.55, 0.34) }, steps: 10);
                Shot("pointer-ink-highlighter.png");

                // Survey's in-progress highlighter already carries mixBlendMode:'multiply'
                // against the page AND every earlier sibling (SVGAnnotationLayer.jsx:5028-5039),
                // so the stroke must not change appearance when the pointer is released. Drawn
                // straight over the committed pen strokes above: preview and commit are shot
                // one frame apart, and any release "pop" shows up as a difference between them.
                editor.PointerDown(P(0.06, 0.16)); Pump(1);
                for (int i = 1; i <= 16; i++) { editor.PointerMove(P(0.06 + 0.24 * i / 16.0, 0.16)); Pump(1); }
                Shot("pointer-highlighter-preview-over-ink.png");
                editor.PointerUp(P(0.30, 0.16)); Pump(4);
                Shot("pointer-highlighter-committed-over-ink.png");

                var inkPaths = canvas.Children.OfType<System.Windows.Shapes.Path>()
                    .Where(q => q.Tag is AnnotationData { Type: AnnotationData.TypeFreehand or AnnotationData.TypeHighlighter })
                    .ToList();
                var selectedAfterInk = typeof(EditorWindow)
                    .GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);
                File.WriteAllText(Path.Combine(outDir, "ink-commit.txt"),
                    $"selectedAfterInk={selectedAfterInk?.GetType().Name ?? "null"}" + Environment.NewLine +
                    string.Join(Environment.NewLine, inkPaths.Select(q =>
                    {
                        var m = (AnnotationData)q.Tag!;
                        return $"type={m.Type} width={m.Thickness} color={m.Color} " +
                               $"fill={q.Fill.GetType().Name} bounds={q.Data!.Bounds} " +
                               $"fillRule={((PathGeometry)q.Data).FillRule}";
                    })));

                // Eraser: the ring is the only cursor, a stationary press already carves,
                // and in PARTIAL mode a non-ink annotation is deleted WHOLE in the same
                // gesture (Survey's 'partial else entire').
                Check("RectangleToolBtn"); Drag(new[] { (0.58, 0.62), (0.90, 0.86) });
                Check("EraserToolBtn"); Pump(3);
                editor.PointerMove(P(0.20, 0.50)); Pump(3);
                Shot("pointer-eraser-cursor.png");
                int childrenBeforeErase = canvas.Children.Count;
                var pressTarget = inkPaths[0];
                double pressAreaBefore = pressTarget.Data!.GetArea();
                Click(0.12, 0.20);                       // press without moving must carve
                double pressAreaAfter = pressTarget.Data!.GetArea();
                Shot("pointer-eraser-press.png");
                Drag(new[] { (0.04, 0.44), (0.50, 0.50) }, steps: 24);
                Shot("pointer-eraser-partial-ink.png");
                Drag(new[] { (0.60, 0.70), (0.70, 0.74) }, steps: 8);
                Shot("pointer-eraser-partial-atomic.png");
                File.WriteAllText(Path.Combine(outDir, "eraser.txt"),
                    $"before={childrenBeforeErase} after={canvas.Children.Count} " +
                    $"pressArea={pressAreaBefore}->{pressAreaAfter} " +
                    $"rects={canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Count()} " +
                    $"cursor={((FrameworkElement)editor.FindName("EditorSurface")!).Cursor} " +
                    $"ring={((FrameworkElement)editor.FindName("EraserCursor")!).Visibility} " +
                    $"halo={((FrameworkElement)editor.FindName("EraserCursorHalo")!).Visibility}");

                // --- Survey shapes parity (this pass). Clean canvas so the chrome is readable. ---
                Check("SelectToolBtn");
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);

                // Big rect (all 8 thumbs) + a tiny one (Survey drops to corners, then to br only).
                Check("RectangleToolBtn");
                Drag(new[] { (0.08, 0.15), (0.40, 0.50) });
                Drag(new[] { (0.50, 0.15), (0.535, 0.19) });   // ~39x25 px: corners only
                Drag(new[] { (0.60, 0.15), (0.607, 0.24) });   // ~8x56 px: br only
                // Unfilled rects only hit on their border, so click the left edge of each.
                Check("SelectToolBtn");
                Click(0.08, 0.32); Shot("pointer-shapes-handles-all.png");
                Click(0.50, 0.17); Shot("pointer-shapes-handles-corners.png");
                Click(0.60, 0.20); Shot("pointer-shapes-handles-single.png");
                Click(0.95, 0.95);

                // Hover halo: the mark's own geometry restroked, no selection.
                editor.PointerMove(P(0.08, 0.32)); Pump(3);
                Shot("pointer-hover-halo.png");
                File.WriteAllText(Path.Combine(outDir, "hover-halo.txt"),
                    $"visible={((FrameworkElement)editor.FindName("HoverHalo")!).Visibility} " +
                    $"width={((System.Windows.Shapes.Path)editor.FindName("HoverHalo")!).StrokeThickness}");
                editor.PointerMove(P(0.95, 0.95)); Pump(3);

                // Dashed / dotted borders: Survey's [6,4] and [2,4] are ABSOLUTE units, so the
                // rhythm must not change with thickness. Two rects at 2 px and 12 px.
                var lineStyleBox = (System.Windows.Controls.ComboBox)editor.FindName("LineStyleBox")!;
                var shapeSizeField = typeof(EditorWindow).GetField("_shapeSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
                // Deselect first: the line-style combo also restyles the current selection, and
                // a freshly committed shape is selected.
                void SelectLineStyle(string tag)
                {
                    InvokePrivate(editor, "Select", null);
                    lineStyleBox.SelectedItem = lineStyleBox.Items.OfType<System.Windows.Controls.ComboBoxItem>()
                        .First(it => (string?)it.Tag == tag);
                }
                Check("RectangleToolBtn");
                SelectLineStyle("dashed"); Pump(2);
                shapeSizeField.SetValue(editor, 2.0); Drag(new[] { (0.08, 0.60), (0.45, 0.72) });
                shapeSizeField.SetValue(editor, 12.0); Drag(new[] { (0.08, 0.78), (0.45, 0.92) });
                SelectLineStyle("dotted"); Pump(2);
                shapeSizeField.SetValue(editor, 2.0); Drag(new[] { (0.52, 0.60), (0.92, 0.72) });
                shapeSizeField.SetValue(editor, 12.0); Drag(new[] { (0.52, 0.78), (0.92, 0.92) });
                SelectLineStyle("solid"); shapeSizeField.SetValue(editor, 2.0); Pump(2);
                Shot("pointer-dash-units.png");
                // The rendered rhythm must be 6/4 (dashed) and 2/4 (dotted) in absolute px at
                // EVERY thickness — WPF stores the array in thickness multiples.
                File.WriteAllText(Path.Combine(outDir, "dash-units.txt"),
                    string.Join(Environment.NewLine, canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
                        .Where(r2 => r2.Tag is AnnotationData { Type: AnnotationData.TypeRectangle }
                                     && r2.StrokeDashArray is { Count: > 0 })
                        .Select(r2 => $"style={AnnotationStyle.LineStyleFrom((AnnotationData)r2.Tag!)} " +
                                      $"thickness={r2.StrokeThickness} " +
                                      $"array=[{string.Join(",", r2.StrokeDashArray)}] " +
                                      $"rendered=[{string.Join(",", r2.StrokeDashArray.Select(v => v * r2.StrokeThickness))}] " +
                                      $"cap={r2.StrokeDashCap}")));

                // Counters: white caption, no ring, radius = Size, renumbering on delete.
                Check("SelectToolBtn");
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                var counterBtn2 = ((StackPanel)editor.FindName("ShapeGroupPanel")!).Children
                    .OfType<RadioButton>().First(rb => rb.Tag as string == "Step");
                var counterSizeField = typeof(EditorWindow).GetField("_counterSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
                counterBtn2.IsChecked = true; Pump(3);
                foreach (double r in new[] { 8.0, 14.0, 24.0 })
                {
                    counterSizeField.SetValue(editor, r);
                    InvokePrivate(editor, "RememberSize", r);
                    Click(0.10 + 0.12 * Array.IndexOf(new[] { 8.0, 14.0, 24.0 }, r), 0.30);
                }
                counterSizeField.SetValue(editor, 14.0);
                InvokePrivate(editor, "RememberSize", 14.0);
                Click(0.50, 0.30); Click(0.62, 0.30);
                Shot("pointer-counters.png");

                string Captions() => string.Join(",", canvas.Children.OfType<Grid>()
                    .Where(g2 => g2.Tag is AnnotationData { Type: AnnotationData.TypeStep })
                    .Select(g2 => g2.Children.OfType<TextBlock>().First().Text));
                string before = Captions();
                Check("SelectToolBtn");
                Click(0.50, 0.30);
                InvokePrivate(editor, "DeleteSelected");
                Pump(4);
                Shot("pointer-counters-renumbered.png");
                File.WriteAllText(Path.Combine(outDir, "counter-renumber.txt"),
                    $"before={before} afterDelete={Captions()}");

                // Selected counter: Survey shows ONE dot on the nubbin tip, no bbox / stem / icon.
                Click(0.10, 0.30); Shot("pointer-counter-chrome.png");
                File.WriteAllText(Path.Combine(outDir, "counter-chrome.txt"),
                    $"selectionRect={((FrameworkElement)editor.FindName("SelectionRect")!).Visibility}");

                // Counter placement is press-drag-release, not a click: the badge follows the
                // cursor, Shift freezes the nub TIP and swings the body around it, releasing
                // Shift resumes the slide with no jump, and Escape cancels outright
                // (Survey PDFViewer.jsx:31727-31790, 26079-26257). Shift/Escape arrive through
                // Survey's window-level capture listener, which is what these private calls are.
                string CounterAngles() => string.Join(",", canvas.Children.OfType<Grid>()
                    .Where(g2 => g2.Tag is AnnotationData { Type: AnnotationData.TypeStep })
                    .Select(g2 => Math.Round(((AnnotationData)g2.Tag!).PointerAngle ?? -1, 1)));
                int CounterCount() => canvas.Children.OfType<Grid>()
                    .Count(g2 => g2.Tag is AnnotationData { Type: AnnotationData.TypeStep });

                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                counterBtn2.IsChecked = true; Pump(3);
                editor.PointerDown(P(0.30, 0.40)); Pump(2);
                editor.PointerMove(P(0.45, 0.40)); Pump(2);
                Shot("pointer-counter-drag-slide.png");
                InvokePrivate(editor, "FreezeCounterTip");              // Shift down
                editor.PointerMove(P(0.45, 0.58)); Pump(2);
                Shot("pointer-counter-drag-orbit.png");
                InvokePrivate(editor, "ReleaseCounterTip");             // Shift up
                editor.PointerMove(P(0.52, 0.58)); Pump(2);
                editor.PointerUp(P(0.52, 0.58)); Pump(4);
                Shot("pointer-counter-drag-committed.png");
                int afterCommit = CounterCount();
                string committedAngle = CounterAngles();
                // Escape mid-drag leaves nothing behind.
                editor.PointerDown(P(0.72, 0.40)); Pump(2);
                editor.PointerMove(P(0.78, 0.46)); Pump(2);
                InvokePrivate(editor, "CancelCounterDrag");
                Pump(4);
                File.WriteAllText(Path.Combine(outDir, "counter-drag.txt"),
                    $"committed={afterCommit} committedAngle={committedAngle} afterEscape={CounterCount()}");

                // Multi-selection: Survey drops the per-item boxes and gives every member the
                // SAME blue glow the hover paints (SVGAnnotationLayer.jsx:3826-3840).
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                Check("RectangleToolBtn");
                Drag(new[] { (0.10, 0.20), (0.35, 0.45) });
                Drag(new[] { (0.45, 0.20), (0.70, 0.45) });
                Check("SelectToolBtn");
                InvokePrivate(editor, "Select", null);
                Drag(new[] { (0.05, 0.12), (0.80, 0.55) });   // window marquee takes both
                Pump(4);
                Shot("pointer-multi-select-halos.png");
                File.WriteAllText(Path.Combine(outDir, "multi-select-halos.txt"),
                    string.Join(Environment.NewLine,
                        ((Canvas)editor.FindName("InteractionCanvas")!).Children
                            .OfType<System.Windows.Shapes.Path>()
                            .Where(pth => pth.Visibility == Visibility.Visible && pth.Data is not null)
                            .Select(pth => $"halo stroke={pth.Stroke} opacity={pth.Opacity} " +
                                           $"width={pth.StrokeThickness} bounds={pth.Data!.Bounds}")));

                // Non-solid arrowheads keep Survey's max(2, strokeWidth) floor on a 1px line
                // (lineRenderHelpers.js:110-176) and never pick up the shaft's dash.
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                Check("ArrowToolBtn");
                var arrowHeadBox = (ComboBox)editor.FindName("ArrowStyleBox")!;
                var shapeSize2 = typeof(EditorWindow).GetField("_shapeSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
                double headRow = 0.18;
                foreach (string headTag in new[] { "openTriangle", "vShape", "openCircle", "horizontalLine" })
                {
                    InvokePrivate(editor, "Select", null);
                    arrowHeadBox.SelectedItem = arrowHeadBox.Items.OfType<ComboBoxItem>()
                        .First(it => (string?)it.Tag == headTag);
                    Pump(2);
                    shapeSize2.SetValue(editor, 1.0);
                    InvokePrivate(editor, "RememberSize", 1.0);
                    Drag(new[] { (0.10, headRow), (0.45, headRow) });
                    shapeSize2.SetValue(editor, 6.0);
                    InvokePrivate(editor, "RememberSize", 6.0);
                    Drag(new[] { (0.55, headRow), (0.90, headRow) });
                    headRow += 0.20;
                }
                Shot("pointer-arrowhead-two-px-floor.png");

                // Rich-text chrome: the Aa button lives in the TOP row and is visible-but-
                // disabled with nothing editable selected (AppShell.jsx:2646-2674); the strip
                // that portals into the 34px sub-row leads with the font-colour swatch
                // (AppShell.jsx:1780-1826).
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                Check("TextToolBtn"); Pump(4);
                var textEditBtn = (Button)editor.FindName("TextEditBtn")!;
                var textEditPanel = (FrameworkElement)editor.FindName("TextEditPanel")!;
                RenderElementToPng(textEditPanel, Path.Combine(outDir, "chrome-text-edit-aa-idle.png"));
                string aaIdle = $"visible={textEditPanel.Visibility} enabled={textEditBtn.IsEnabled} tip={textEditBtn.ToolTip}";
                Click(0.30, 0.30);                    // places a text box and opens its editor
                Pump(6);
                Shot("pointer-rich-text-subrow.png");
                RenderElementToPng((FrameworkElement)editor.FindName("EditorRichTextHost")!,
                    Path.Combine(outDir, "chrome-rich-text-strip.png"));
                var stylePanel = (StackPanel)editor.FindName("TextStylePanel")!;
                var fontColorBtn = (Button)editor.FindName("FontColorBtn")!;
                File.WriteAllText(Path.Combine(outDir, "rich-text-strip.txt"),
                    $"aaIdle: {aaIdle}" + Environment.NewLine +
                    $"aaEditing: enabled={textEditBtn.IsEnabled} tip={textEditBtn.ToolTip}" + Environment.NewLine +
                    $"stripFirstChildHoldsFontColor={((FrameworkElement)stylePanel.Children[0]).IsAncestorOf(fontColorBtn)}" + Environment.NewLine +
                    $"fontColorFocusable={fontColorBtn.Focusable} boldFocusable={((Button)editor.FindName("BoldBtn")!).Focusable}");
                InvokePrivate(editor, "CommitText");
                Pump(4);

                // ---- CHROME: Survey's flat strip, gold accent, and the new popovers ----
                InvokePrivate(editor, "Select", null);
                canvas.Children.Clear();
                Pump(4);
                Check("RectangleToolBtn"); Pump(4);
                Shot("pointer-chrome-shapes-row.png");
                // The tool cluster: `.btn-icon` squares, the #5a6473 divider, and the
                // gold `.btn-active` glyph on the live category button.
                RenderElementToPng(editor.FindName("ToolPanel") as UIElement,
                    Path.Combine(outDir, "chrome-tool-cluster.png"));

                // Cloud border reveals the Bump field; the size popover shows preset previews.
                var lineBox = (ComboBox)editor.FindName("LineStyleBox")!;
                foreach (ComboBoxItem ci in lineBox.Items.OfType<ComboBoxItem>())
                    if (ci.Tag as string == "cloud") { lineBox.SelectedItem = ci; break; }
                InvokePrivate(editor, "UpdateContextPanels");
                Pump(4);
                Shot("pointer-chrome-cloud-bump.png");
                RenderElementToPng(editor.FindName("SizeField") as UIElement,
                    Path.Combine(outDir, "chrome-size-field.png"));
                ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("SizePresetsBtn")!).IsChecked = true;
                Pump(6);
                RenderElementToPng(((System.Windows.Controls.Primitives.Popup)editor.FindName("SizePresetsPopup")!).Child,
                    Path.Combine(outDir, "chrome-size-popover.png"));
                ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("SizePresetsBtn")!).IsChecked = false;
                Pump(3);

                // The annotation dropdown popover: heading rule, 34px rows, is-active wash.
                var arrowBox = (ComboBox)editor.FindName("ArrowStyleBox")!;
                Check("ArrowToolBtn"); Pump(4);
                arrowBox.IsDropDownOpen = true; Pump(8);
                RenderElementToPng(arrowBox, Path.Combine(outDir, "chrome-dropdown-trigger.png"));
                arrowBox.IsDropDownOpen = false; Pump(3);

                // Eraser split menu, and the counter colour-swatch variant.
                Check("FreehandToolBtn"); Pump(3);
                ((RadioButton)editor.FindName("EraserToolBtn")!).IsChecked = true; Pump(4);
                ((System.Windows.Controls.Primitives.Popup)editor.FindName("EraserModePopup")!).IsOpen = true;
                Pump(6);
                RenderElementToPng(((System.Windows.Controls.Primitives.Popup)editor.FindName("EraserModePopup")!).Child,
                    Path.Combine(outDir, "chrome-eraser-split-menu.png"));
                ((System.Windows.Controls.Primitives.Popup)editor.FindName("EraserModePopup")!).IsOpen = false;
                Pump(3);
                counterBtn2.IsChecked = true; Pump(4);
                Shot("pointer-chrome-counter-row.png");
                RenderElementToPng(editor.FindName("ColorWellBtn") as UIElement,
                    Path.Combine(outDir, "chrome-color-swatch-counter.png"));

                // ---- SWEEP PROBE: one shared preset palette, tooltip delay, small counters ----
                {
                    var sweep = new System.Text.StringBuilder();

                    // AREA-04/05: the stroke popover must be Survey's 16-cell PRESET_COLORS grid
                    // (CompactColorPicker.jsx:13-18) with a 2px white ring on the applied colour.
                    typeof(EditorWindow).GetField("_customColor", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(editor, Color.FromRgb(0x00, 0xFF, 0x80));
                    ((RadioButton)editor.FindName("CustomSwatch")!).RaiseEvent(
                        new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(8);
                    var strokeGrid = (System.Windows.Controls.Primitives.UniformGrid)editor.FindName("ColorPickerGrid")!;
                    RenderElementToPng(((System.Windows.Controls.Primitives.Popup)editor.FindName("ColorPickerPopup")!).Child,
                        Path.Combine(outDir, "sweep-color-presets.png"), dpi: 192);
                    string Cells(System.Windows.Controls.Primitives.UniformGrid g) =>
                        string.Join(", ", g.Children.OfType<Button>().Select(b =>
                            $"{(b.Tag is Color c ? AnnotationStyle.ToRgbHex(c) : "?")}" +
                            $"{(b.BorderThickness.Left > 1 ? "*" : "")}"));
                    sweep.AppendLine($"[COLOR] strokeCells={strokeGrid.Children.Count} columns={strokeGrid.Columns}");
                    sweep.AppendLine($"[COLOR] stroke: {Cells(strokeGrid)}");
                    ((System.Windows.Controls.Primitives.Popup)editor.FindName("ColorPickerPopup")!).IsOpen = false;
                    Pump(3);

                    // AREA-06: the font-colour popover is the SAME palette, not a private one.
                    InvokePrivate(editor, "BuildFontColorPalette");
                    Pump(3);
                    var fontGrid = (System.Windows.Controls.Primitives.UniformGrid)editor.FindName("FontColorGrid")!;
                    ((System.Windows.Controls.Primitives.Popup)editor.FindName("FontColorPopup")!).IsOpen = true;
                    Pump(6);
                    RenderElementToPng(((System.Windows.Controls.Primitives.Popup)editor.FindName("FontColorPopup")!).Child,
                        Path.Combine(outDir, "sweep-font-color-presets.png"), dpi: 192);
                    sweep.AppendLine($"[COLOR] fontCells={fontGrid.Children.Count}");
                    sweep.AppendLine($"[COLOR] font:   {Cells(fontGrid)}");
                    sweep.AppendLine($"[COLOR] sameList={Cells(strokeGrid).Replace("*", "") == Cells(fontGrid).Replace("*", "")}");
                    ((System.Windows.Controls.Primitives.Popup)editor.FindName("FontColorPopup")!).IsOpen = false;
                    Pump(3);

                    // AREA-01: with the app-wide 0ms metadata installed (App.InstallInstantTooltips),
                    // no Style may reintroduce a delay on top of it — Survey shows every chrome
                    // tooltip on mouseenter with no timer (Tooltip.jsx:22-31, 144-161).
                    typeof(App).GetMethod("InstallInstantTooltips", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, null);
                    var toolBtn = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("FreehandToolBtn")!;
                    var styled = new System.Windows.Controls.Primitives.ToggleButton
                    {
                        Style = (Style)editor.FindResource("ToolButton"),
                        ToolTip = "Top left",
                    };
                    styled.Measure(new Size(100, 100));
                    sweep.AppendLine($"[TIP] toolRail={ToolTipService.GetInitialShowDelay(toolBtn)}ms " +
                        $"ToolButtonStyle={ToolTipService.GetInitialShowDelay(styled)}ms");

                    // AREA-07: the counter digit must stay proportional at the minimum radius.
                    canvas.Children.Clear();
                    InvokePrivate(editor, "Select", null);
                    counterBtn2.IsChecked = true; Pump(3);
                    var stepStart = (TextBox)editor.FindName("StepStartInput")!;
                    stepStart.Text = "100";
                    InvokePrivate(editor, "CommitStepStart");
                    Pump(3);
                    foreach (double size in new[] { 4.0, 8.0, 20.0 })
                    {
                        InvokePrivate(editor, "RememberSize", size);
                        Pump(2);
                        Click(0.15 + 0.25 * (size / 20.0), 0.40);
                        sweep.AppendLine($"[COUNTER] size={size} radius={CounterGeometry.RadiusFor(size)} " +
                            $"font(1)={CounterGeometry.FontSize(CounterGeometry.RadiusFor(size), "1"):F2} " +
                            $"font(100)={CounterGeometry.FontSize(CounterGeometry.RadiusFor(size), "100"):F2}");
                    }
                    Pump(4);
                    Shot("sweep-counter-sizes.png");
                    RenderToPng(editor, Path.Combine(outDir, "sweep-counter-sizes-400pct.png"), dpi: 384);

                    // AREA-10: Survey caps history at 100 entries (PDFViewer.jsx:23652). Trimmed
                    // entries must be Discard()ed so blur/pixelate backup bitmaps are released
                    // instead of being held for the life of the window.
                    var undoStack = (System.Collections.ICollection)typeof(EditorWindow)
                        .GetField("_undoStack", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    var push = typeof(EditorWindow).GetMethod("Push", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    int depthBefore = undoStack.Count;
                    int discarded = 0;
                    for (int i = 0; i < 130; i++)
                        push.Invoke(editor, new object[]
                        {
                            new EditorAction(() => { }, () => { }, () => discarded++),
                            false,
                        });
                    sweep.AppendLine($"[HISTORY] before={depthBefore} pushed=130 depth={undoStack.Count} " +
                        $"discarded={discarded}");
                    File.WriteAllText(Path.Combine(outDir, "sweep.txt"), sweep.ToString());
                }

                editor.Close();
                Pump(2);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static SD.Bitmap CreateSanitizedSource()
    {
        var bmp = new SD.Bitmap(1120, 620, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
        using (var title = new SD.Font("Segoe UI", 25, SD.FontStyle.Bold))
        using (var body = new SD.Font("Segoe UI", 15, SD.FontStyle.Regular))
        using (var small = new SD.Font("Segoe UI", 12, SD.FontStyle.Regular))
        {
            g.Clear(SD.Color.FromArgb(245, 247, 251));
            g.FillRectangle(new SD.SolidBrush(SD.Color.FromArgb(36, 46, 66)), 0, 0, 1120, 78);
            g.DrawString("SANITIZED EDITOR BASELINE", title, SD.Brushes.White, 38, 20);
            g.DrawString("Synthetic content only", small, SD.Brushes.LightGray, 835, 31);

            DrawCard(g, new SD.Rectangle(55, 115, 300, 180), "Annotation tools", body);
            DrawCard(g, new SD.Rectangle(410, 115, 300, 180), "Selection + resize", body);
            DrawCard(g, new SD.Rectangle(765, 115, 300, 180), "Local privacy", body);
            DrawCard(g, new SD.Rectangle(55, 340, 1010, 215), "Source-resolution canvas", body);

            g.DrawString("Arrow  Shape  Pen  Text", body, SD.Brushes.DimGray, 82, 182);
            g.DrawString("Move  Handles  Z-order", body, SD.Brushes.DimGray, 437, 182);
            g.DrawString("Blur sample text", body, SD.Brushes.DimGray, 792, 170);
            g.DrawString("Pixelate sample text", body, SD.Brushes.DimGray, 792, 226);
            g.DrawString("All annotations stay on this PC. Export remains source-sized.", body,
                SD.Brushes.DimGray, 90, 435);
        }

        BitmapEffects.Blur(bmp, new SD.Rectangle(785, 160, 245, 36), radius: 6);
        BitmapEffects.PixelateRandomized(bmp, new SD.Rectangle(785, 216, 245, 36), seed: 20260731, cellSize: 12);
        return bmp;
    }

    private static void DrawCard(SD.Graphics g, SD.Rectangle bounds, string heading, SD.Font font)
    {
        using var fill = new SD.SolidBrush(SD.Color.White);
        using var border = new SD.Pen(SD.Color.FromArgb(214, 220, 232), 2);
        g.FillRectangle(fill, bounds);
        g.DrawRectangle(border, bounds);
        g.DrawString(heading, font, SD.Brushes.DarkSlateGray, bounds.X + 25, bounds.Y + 22);
    }

    private static UIElement AddRepresentativeAnnotations(Canvas canvas)
    {
        UIElement Add(AnnotationData data)
        {
            var element = ProjectSerializer.CreateElement(data, Array.Empty<BitmapSource>());
            if (element is FrameworkElement fe) fe.Tag = data;
            canvas.Children.Add(element);
            return element;
        }

        Add(new AnnotationData
        {
            Type = AnnotationData.TypeArrow,
            Points = new[] { new[] { 80d, 315d }, new[] { 305d, 315d } },
            Color = "#FFFF453A",
            Thickness = 6,
            Style = ArrowStyle.Straight.ToString(),
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeCurvedArrow,
            Points = new[] { new[] { 415d, 325d }, new[] { 555d, 245d }, new[] { 690d, 325d } },
            Color = "#FF0A84FF",
            Thickness = 5,
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeLine,
            Points = new[] { new[] { 90d, 505d }, new[] { 345d, 455d } },
            Color = "#FF34C759",
            Thickness = 4,
            LineStyle = "dashed",
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeArrow,
            Points = new[] { new[] { 760d, 500d }, new[] { 980d, 500d } },
            Color = "#FFFF2D55",
            Thickness = 4,
            Head = "vShape",
            LineStyle = "dotted",
        });
        Add(AnnotationData.ForCallout(
            CalloutLayout.FromDrag(new Point(720, 200), new Point(860, 90)),
            "Callout", Color.FromRgb(255, 69, 58), Color.FromArgb(0x40, 255, 255, 255),
            2, ArrowheadStyle.SolidTriangle, LineBorderStyle.Solid, 18));

        var rectangle = Add(new AnnotationData
        {
            Type = AnnotationData.TypeRectangle,
            Rect = new[] { 405d, 380d, 310d, 135d },
            Color = "#FFFF9500",
            Thickness = 5,
            Fill = ShapeFillMode.Quarter.ToString(),
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeEllipse,
            Rect = new[] { 765d, 345d, 255d, 165d },
            Color = "#FFAF52DE",
            Thickness = 5,
            Fill = ShapeFillMode.None.ToString(),
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeFreehand,
            Points = new[]
            {
                new[] { 85d, 565d }, new[] { 135d, 535d }, new[] { 185d, 570d },
                new[] { 235d, 535d }, new[] { 290d, 565d },
            },
            Color = "#FFFF2D55",
            Thickness = 5,
        });
        Add(new AnnotationData
        {
            Type = AnnotationData.TypeHighlighter,
            Points = new[] { new[] { 420d, 525d }, new[] { 700d, 525d } },
            Color = "#66FFCC00",
            Thickness = 18,
        });
        Add(AnnotationData.ForText(new Point(105, 235), "Pill text", TextStyle.Pill, 24,
            Color.FromRgb(255, 69, 58)));
        Add(AnnotationData.ForText(new Point(450, 235), "Outline", TextStyle.Outline, 26,
            Color.FromRgb(10, 132, 255)));
        Add(AnnotationData.ForStep(new Point(850, 275), 1, Color.FromRgb(52, 199, 89), 5));
        Add(AnnotationData.ForEmoji(new Point(970, 265), "\U0001F642"));
        return rectangle;
    }

    private static void SelectTool(EditorWindow editor, EditorTool tool) =>
        InvokePrivate(editor, "CheckToolButton", tool);

    private static object? InvokePrivate(EditorWindow editor, string name, object? argument)
    {
        var method = typeof(EditorWindow).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(EditorWindow).FullName, name);
        return method.Invoke(editor, new[] { argument });
    }

    private static object? InvokePrivate(EditorWindow editor, string name)
    {
        var method = typeof(EditorWindow).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(EditorWindow).FullName, name);
        return method.Invoke(editor, null);
    }

    private static void RenderToPng(Window window, string path, double dpi = 96)
    {
        window.UpdateLayout();
        int width = (int)Math.Ceiling(window.ActualWidth * dpi / 96);
        int height = (int)Math.Ceiling(window.ActualHeight * dpi / 96);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        SaveBitmap(bitmap, path);
    }

    private static void RenderElementToPng(UIElement element, string path, double dpi = 96)
    {
        element.UpdateLayout();
        var size = element.RenderSize;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96),
            dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        SaveBitmap(bitmap, path);
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump(int iterations)
    {
        for (int n = 0; n < iterations; n++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }
}
