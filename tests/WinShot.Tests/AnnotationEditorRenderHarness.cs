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
                Click(0.55, 0.735); Shot("pointer-arrow-selected.png");
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

                // Text: click to open, type, commit by clicking away, then reselect.
                Check("TextToolBtn");
                Click(0.10, 0.92); Pump(4);
                var textBox = canvas.Children.OfType<TextBox>().Last();
                textBox.Text = "Hello gjpq text"; Pump(3);
                Shot("pointer-text-editing.png");
                Click(0.02, 0.02); Pump(4);
                Shot("pointer-text-committed.png");

                // Callout: drag tip→box, type, commit, select.
                Check("CalloutToolBtn");
                Drag(new[] { (0.60, 0.95), (0.85, 0.88) }); Pump(4);
                var calloutBox = canvas.Children.OfType<TextBox>().Last();
                calloutBox.Text = "Callout gjpq"; Pump(3);
                Shot("pointer-callout-editing.png");
                Click(0.02, 0.02); Pump(4);
                Check("SelectToolBtn");
                Click(0.85, 0.88); Shot("pointer-callout-selected.png");
                Click(0.10, 0.92); Shot("pointer-text-selected.png");
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
                DragAbs(br, br + new Vector(0.05 * W, 0.03 * H)); Shot("pointer-callout-resize.png");
                Point boxCentre = CalloutPt(l => new Point(l.Box.X + l.Box.Width / 2, l.Box.Y + l.Box.Height / 2));
                DragAbs(boxCentre, boxCentre + new Vector(-0.06 * W, -0.10 * H)); Shot("pointer-callout-moved.png");
                Click(0.02, 0.02);

                // --- Rotation: hover the grip (pill), drag it (live angle), release. ---
                Click(0.10, 0.40);
                Point grip = (Point)typeof(EditorWindow).GetField("_gripContent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                editor.PointerMove(PA(grip)); Pump(3); Shot("pointer-rotation-hover.png");
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

    private static void RenderToPng(Window window, string path, double dpi = 96)
    {
        window.UpdateLayout();
        int width = (int)Math.Ceiling(window.ActualWidth * dpi / 96);
        int height = (int)Math.Ceiling(window.ActualHeight * dpi / 96);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        SaveBitmap(bitmap, path);
    }

    private static void RenderElementToPng(UIElement element, string path)
    {
        element.UpdateLayout();
        var size = element.RenderSize;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
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
