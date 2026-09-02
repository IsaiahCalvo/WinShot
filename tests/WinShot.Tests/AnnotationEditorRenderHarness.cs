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

                // Fit check: the whole image must sit inside the viewport on open.
                {
                    Point tl = canvas.TranslatePoint(new Point(0, 0), viewport);
                    Point brPt = canvas.TranslatePoint(new Point(W, H), viewport);
                    File.WriteAllText(Path.Combine(outDir, "fit.txt"),
                        $"image={tl}..{brPt} viewport={viewport.ActualWidth}x{viewport.ActualHeight}");
                }
                Shot("pointer-open.png");

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
