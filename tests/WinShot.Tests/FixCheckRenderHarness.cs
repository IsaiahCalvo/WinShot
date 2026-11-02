using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// Renders the exact states Isaiah reported (2026-11-02) so each fix is LOOKED AT, not
/// assumed: arrowheads at two widths, the arrow / polygon while being drawn, the hover glow
/// on heads, the quick bar near the top and next to a rotate grip, and the pin icon.
/// Gated; CI prints the PNGs so they can be read back from the job log.
/// </summary>
public class FixCheckRenderHarness
{
    [Fact]
    public void RenderReportedFixStates()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_FIXCHECK") != "1") return;
        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-fixcheck");
        Directory.CreateDirectory(outDir);
        foreach (string stale in Directory.GetFiles(outDir, "*.png")) File.Delete(stale);

        Exception? failure = null;
        var log = new System.Text.StringBuilder();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();
                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var source = new SD.Bitmap(900, 560, SD.Imaging.PixelFormat.Format32bppArgb);
                using (var g = SD.Graphics.FromImage(source)) g.Clear(SD.Color.FromArgb(235, 238, 242));
                var editor = new EditorWindow(source, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000, Top = -6000, Width = 1200, Height = 820, ShowInTaskbar = false,
                };
                editor.Show();
                Pump(60);

                var canvas = (Canvas)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;
                EditorWindow.PointerArgs P(double x, double y) =>
                    new(new Point(x, y), canvas.TranslatePoint(new Point(x, y), viewport), MouseButton.Left, 1);
                // Capturing the mouse on a press makes WPF raise a move at the REAL cursor, which
                // for this off-screen window is thousands of px away; a move back to the press
                // point undoes that before anything is judged.
                void Down(double x, double y) { editor.PointerDown(P(x, y)); Pump(1); editor.PointerMove(P(x, y)); Pump(1); }
                void Move(double x, double y) { editor.PointerMove(P(x, y)); Pump(2); }
                void Up(double x, double y) { editor.PointerUp(P(x, y)); Pump(3); }
                void Drag(double x1, double y1, double x2, double y2)
                {
                    Down(x1, y1);
                    for (int i = 1; i <= 8; i++) Move(x1 + (x2 - x1) * i / 8, y1 + (y2 - y1) * i / 8);
                    Up(x2, y2);
                }
                void Tool(EditorTool t) { Invoke(editor, "CheckToolButton", t); Pump(3); }
                void Field(string name, object value) =>
                    typeof(EditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);
                void Size(double s) { Invoke(editor, "ApplySize", s); Pump(2); }
                void LineStyle(string tag)
                {
                    var box = (ComboBox)editor.FindName("LineStyleBox")!;
                    box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == tag);
                    Pump(2);
                }
                void Deselect() { Tool(EditorTool.Select); InvokeN(editor, "Select", new object?[] { null }); Pump(2); }
                // Window crop around a canvas rect, rendered at 2x so heads can be judged.
                void Shot(string name, Rect canvasRect, double pad = 30)
                {
                    editor.UpdateLayout();
                    const double dpi = 192;
                    var bmp = new RenderTargetBitmap((int)(editor.ActualWidth * 2), (int)(editor.ActualHeight * 2), dpi, dpi, PixelFormats.Pbgra32);
                    bmp.Render(editor);
                    Point a = canvas.TranslatePoint(canvasRect.TopLeft, editor);
                    Point b = canvas.TranslatePoint(canvasRect.BottomRight, editor);
                    var r = new Int32Rect(
                        Math.Max(0, (int)((Math.Min(a.X, b.X) - pad) * 2)), Math.Max(0, (int)((Math.Min(a.Y, b.Y) - pad) * 2)), 0, 0);
                    r.Width = Math.Min(bmp.PixelWidth - r.X, (int)((Math.Abs(b.X - a.X) + pad * 2) * 2));
                    r.Height = Math.Min(bmp.PixelHeight - r.Y, (int)((Math.Abs(b.Y - a.Y) + pad * 2) * 2));
                    Save(new CroppedBitmap(bmp, r), Path.Combine(outDir, name));
                }
                void WindowShot(string name)
                {
                    editor.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(editor);
                    Save(bmp, Path.Combine(outDir, name));
                }
                // Zoom 100 % so canvas px == screen px in the crops.
                var centre = InvokeN(editor, "ViewportCentre", Array.Empty<object?>())!;
                InvokeN(editor, "ZoomAt", new object?[] { centre, 1.0 });
                Pump(4);

                // 1. Heads at 2 and 8: solid, open triangle, open circle.
                Tool(EditorTool.Arrow);
                LineStyle("solid");
                var heads = new[] { ArrowheadStyle.SolidTriangle, ArrowheadStyle.OpenTriangle, ArrowheadStyle.OpenCircle };
                for (int w = 0; w < 2; w++)
                {
                    Size(w == 0 ? 2 : 8);
                    for (int h = 0; h < heads.Length; h++)
                    {
                        Field("_arrowhead", heads[h]);
                        Tool(EditorTool.Arrow);
                        double y = 60 + h * 50 + w * 160;
                        Drag(60, y, 260, y - 20);
                    }
                }
                Deselect();
                Shot("1-heads.png", new Rect(40, 20, 260, 340));

                // 2. An arrow and a line WHILE being drawn, solid then dashed.
                Size(4);
                Field("_arrowhead", ArrowheadStyle.SolidTriangle);
                Tool(EditorTool.Arrow);
                LineStyle("solid");
                Down(380, 80); for (int i = 1; i <= 6; i++) Move(380 + i * 30, 80 + i * 8);
                Shot("2a-arrow-mid-drag-solid.png", new Rect(360, 60, 220, 80));
                Up(560, 128);
                LineStyle("dashed");
                Tool(EditorTool.Arrow);
                Down(380, 180); for (int i = 1; i <= 6; i++) Move(380 + i * 30, 180 + i * 8);
                Shot("2b-arrow-mid-drag-dashed.png", new Rect(360, 160, 220, 80));
                Up(560, 228);
                LineStyle("solid");
                Tool(EditorTool.Line);
                Down(380, 280); for (int i = 1; i <= 6; i++) Move(380 + i * 30, 280 + i * 8);
                Shot("2c-line-mid-drag-solid.png", new Rect(360, 260, 220, 80));
                Up(560, 328);
                Deselect();

                // 3. A polygon draft: three corners placed, cursor at a fourth.
                Tool(EditorTool.Polygon);
                LineStyle("solid");
                foreach (var (x, y) in new[] { (640.0, 60.0), (820.0, 80.0), (800.0, 200.0) }) { Down(x, y); Up(x, y); }
                Move(660, 220);
                Shot("3-polygon-draft.png", new Rect(620, 40, 220, 200));
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump(3);

                // 4. Hover glow on an open-circle head and a solid head (the 8 px arrows of #1).
                Deselect();
                Move(260 + 1, 60 + 160 + 2 * 50 - 20);     // open circle end at w=8
                Shot("4a-hover-open-circle.png", new Rect(40, 280, 260, 80));
                Move(150, 60 + 160 - 10);                  // solid arrow shaft at w=8
                Shot("4b-hover-solid.png", new Rect(40, 180, 260, 80));
                Move(880, 540);

                // 5. Quick bar: a rectangle at the very top of the image, then one lower down.
                Tool(EditorTool.Rectangle);
                Size(3);
                Drag(420, 4, 560, 60);
                Tool(EditorTool.Select);
                Down(420, 30); Up(420, 30);
                Pump(6);
                WindowShot("5a-quickbar-top.png");
                Deselect();
                Tool(EditorTool.Rectangle);
                Drag(300, 400, 460, 470);
                Tool(EditorTool.Select);
                Down(300, 430); Up(300, 430);
                Pump(6);
                WindowShot("5b-quickbar-mid.png");

                // 6. Bottom bar (pin icon), full window at 2x.
                var bar = (FrameworkElement)editor.FindName("BtnPin")!;
                Point pin = bar.TranslatePoint(new Point(0, 0), editor);
                {
                    var bmp = new RenderTargetBitmap((int)(editor.ActualWidth * 2), (int)(editor.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    bmp.Render(editor);
                    int rx = Math.Max(0, (int)((pin.X - 120) * 2));
                    int ry = Math.Max(0, (int)((pin.Y - 10) * 2));
                    var r = new Int32Rect(rx, ry, Math.Min(bmp.PixelWidth - rx, 220 * 2), Math.Min(bmp.PixelHeight - ry, 50 * 2));
                    Save(new CroppedBitmap(bmp, r), Path.Combine(outDir, "6-bottom-bar.png"));
                }

                // 7. Rail footer: the snapping switch on, then off.
                void RailShot(string name)
                {
                    var btn = (FrameworkElement)editor.FindName("SnappingBtn")!;
                    Point at = btn.TranslatePoint(new Point(0, 0), editor);
                    editor.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)(editor.ActualWidth * 2), (int)(editor.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    bmp.Render(editor);
                    int rx = Math.Max(0, (int)((at.X - 20) * 2));
                    int ry = Math.Max(0, (int)((at.Y - 120) * 2));
                    var r = new Int32Rect(rx, ry, Math.Min(bmp.PixelWidth - rx, 80 * 2), Math.Min(bmp.PixelHeight - ry, 200 * 2));
                    Save(new CroppedBitmap(bmp, r), Path.Combine(outDir, name));
                }
                Deselect();
                RailShot("7a-snapping-on.png");
                Invoke(editor, "ToggleSnapping", null); Pump(3);
                RailShot("7b-snapping-off.png");
                Invoke(editor, "ToggleSnapping", null); Pump(3);

                // 8. Group resize: three marks selected together, then the frame dragged by a
                // corner and by the right side.
                Size(3);
                Tool(EditorTool.Rectangle); Drag(640, 300, 720, 360);
                Tool(EditorTool.Ellipse); Drag(740, 300, 800, 350);
                Tool(EditorTool.Arrow); Drag(640, 400, 780, 430);
                Tool(EditorTool.Select);
                Drag(625, 285, 870, 450);
                Pump(4);
                var sel = (FrameworkElement)editor.FindName("SelectionRect")!;
                Rect Frame() => new(Canvas.GetLeft(sel), Canvas.GetTop(sel), sel.Width, sel.Height);
                string Boxes()
                {
                    var parts = new List<string>();
                    foreach (UIElement el in canvas.Children)
                        if (el is FrameworkElement { Tag: AnnotationData d } fe && fe.TranslatePoint(new Point(0, 0), canvas) is var p
                            && VisualTreeHelper.GetDescendantBounds(fe) is var lb && !lb.IsEmpty)
                        {
                            var gb = fe.TransformToAncestor(canvas).TransformBounds(lb);
                            if (gb.Left >= 600 && gb.Top >= 270) parts.Add($"{d.Type}:{gb.X:0},{gb.Y:0},{gb.Width:0}x{gb.Height:0}");
                        }
                    return string.Join(" ", parts);
                }
                Rect f0 = Frame();
                log.AppendLine($"group before frame={f0} marks={Boxes()}");
                WindowShot("8a-group-selected.png");
                Drag(f0.Right, f0.Bottom, f0.Right + 60, f0.Bottom + 45);
                Rect f1 = Frame();
                log.AppendLine($"group corner frame={f1} marks={Boxes()}");
                WindowShot("8b-group-corner.png");
                Drag(f1.Right, f1.Top + f1.Height / 2, f1.Right - 70, f1.Top + f1.Height / 2);
                Rect f2 = Frame();
                log.AppendLine($"group side frame={f2} marks={Boxes()}");
                WindowShot("8c-group-side.png");
                Invoke(editor, "UndoAsync", null); Pump(4);
                Invoke(editor, "UndoAsync", null); Pump(4);
                log.AppendLine($"group undone frame={Frame()} marks={Boxes()}");

                // 9. A single resize pulls its edge to a neighbour: the rectangle's right side
                // dragged to 3 px short of the ellipse's left edge (740) should land on it.
                Deselect();
                Tool(EditorTool.Select);
                string State()
                {
                    object? F(string n) => typeof(EditorWindow).GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor);
                    var selList = (System.Collections.IList)F("_selection")!;
                    return $"sel={selList.Count} kind={F("_handleKind")} active={F("_activeHandle")} moving={F("_movingSelection")} moveTotal={F("_moveTotal")} marks={Boxes()}";
                }
                log.AppendLine($"9 start {State()}");
                Down(640, 330); log.AppendLine($"9 down-on-rect {State()}");
                Up(640, 330); log.AppendLine($"9 up {State()}");
                Pump(3);
                Down(720, 330); log.AppendLine($"9 down-on-handle {State()}");
                for (int i = 1; i <= 8; i++) { Move(720 + 17.0 * i / 8, 330); if (i == 1 || i == 8) log.AppendLine($"9 move{i} {State()}"); }
                Up(737, 330);
                log.AppendLine($"single snap {State()}");
                WindowShot("9-single-snap.png");

                // 10. Group rotate: the same three marks turned by the frame's grip, mid-drag
                // (frame turns rigidly) and after release.
                Deselect();
                Tool(EditorTool.Select);
                Drag(625, 285, 870, 450);
                Pump(4);
                var grip = (FrameworkElement)typeof(EditorWindow).GetField("_rotateThumb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                Point g0 = new(Canvas.GetLeft(grip) + grip.Width / 2, Canvas.GetTop(grip) + grip.Height / 2);
                Rect fr = Frame();
                Point piv = new(fr.Left + fr.Width / 2, fr.Top + fr.Height / 2);
                log.AppendLine($"rotate grip={g0} visible={grip.Visibility} pivot={piv}");
                Down(g0.X, g0.Y);
                double rad0 = Math.Atan2(g0.Y - piv.Y, g0.X - piv.X);
                double radius = (g0 - piv).Length;
                for (int i = 1; i <= 8; i++)
                {
                    double a = rad0 + (30.0 * Math.PI / 180) * i / 8;
                    Move(piv.X + radius * Math.Cos(a), piv.Y + radius * Math.Sin(a));
                }
                WindowShot("10a-group-rotate-mid.png");
                double aEnd = rad0 + 30.0 * Math.PI / 180;
                Up(piv.X + radius * Math.Cos(aEnd), piv.Y + radius * Math.Sin(aEnd));
                log.AppendLine($"rotate after marks={Boxes()}");
                WindowShot("10b-group-rotate-after.png");

                // 11. Page-edge rules carried from the page-edge branch: grabbers stay whole at
                // the image edge, and a resize or turn stops at the edge (single and group).
                string BoxesIn(Rect area)
                {
                    var parts = new List<string>();
                    foreach (UIElement el in canvas.Children)
                        if (el is FrameworkElement { Tag: AnnotationData d } fe
                            && VisualTreeHelper.GetDescendantBounds(fe) is var lb && !lb.IsEmpty)
                        {
                            var gb = fe.TransformToAncestor(canvas).TransformBounds(lb);
                            if (area.Contains(new Point(gb.X + gb.Width / 2, gb.Y + gb.Height / 2)))
                                parts.Add($"{d.Type}:{gb.X:0.#},{gb.Y:0.#},{gb.Width:0.#}x{gb.Height:0.#}");
                        }
                    return string.Join(" ", parts);
                }
                // 11a. One rectangle near the bottom; its corner pulled 70 px past the bottom
                // edge and 80 px right. The bottom should stop at 560, the right still follow.
                Deselect();
                Tool(EditorTool.Rectangle); Drag(40, 470, 120, 530);
                var rect11a = canvas.Children[canvas.Children.Count - 1];
                Tool(EditorTool.Select);
                InvokeN(editor, "Select", new object?[] { rect11a }); Pump(3);
                var area11a = new Rect(0, 440, 400, 200);
                Rect s11 = Frame();
                log.AppendLine($"11a before frame={s11} {BoxesIn(area11a)}");
                Drag(s11.Right, s11.Bottom, s11.Right + 80, s11.Bottom + 70);
                log.AppendLine($"11a after {BoxesIn(area11a)} (bottom should be <= 560, right ~200)");
                WindowShot("11a-single-resize-edge.png");

                // 11b. A mark at the very top: grabbers draw whole over the desk, rotate grip below.
                Deselect();
                Tool(EditorTool.Rectangle); Drag(620, 2, 720, 50);
                var rect11b = canvas.Children[canvas.Children.Count - 1];
                Tool(EditorTool.Select);
                InvokeN(editor, "Select", new object?[] { rect11b }); Pump(3);
                Shot("11b-top-edge-handles.png", new Rect(600, -30, 140, 130), 20);
                var grip11 = (FrameworkElement)typeof(EditorWindow).GetField("_rotateThumb", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                log.AppendLine($"11b grip top={Canvas.GetTop(grip11):0.#} (below the mark means > 50)");

                // 11c. Two marks, group corner dragged 80 px past the left edge: union stops at 0.
                Deselect();
                Tool(EditorTool.Rectangle); Drag(40, 380, 120, 430);
                Tool(EditorTool.Ellipse); Drag(160, 380, 240, 440);
                Tool(EditorTool.Select);
                Drag(25, 365, 260, 455);
                Pump(4);
                var area11c = new Rect(0, 360, 300, 100);
                Rect g11 = Frame();
                log.AppendLine($"11c before frame={g11} {BoxesIn(area11c)}");
                Drag(g11.Left, g11.Bottom, g11.Left - 80, g11.Bottom);
                log.AppendLine($"11c after frame={Frame()} {BoxesIn(new Rect(-200, 300, 600, 200))} (left should be >= 0)");
                WindowShot("11c-group-resize-edge.png");

                // 11d. A wide group near the top turned 45 degrees by its grip: it stops at the
                // last angle that keeps it inside the top edge.
                Deselect();
                Tool(EditorTool.Rectangle); Drag(300, 12, 420, 40);
                Tool(EditorTool.Rectangle); Drag(440, 12, 560, 40);
                Tool(EditorTool.Select);
                Drag(290, 6, 575, 48);
                Pump(4);
                Point gd = new(Canvas.GetLeft(grip11) + grip11.Width / 2, Canvas.GetTop(grip11) + grip11.Height / 2);
                Rect fd = Frame();
                Point pd = new(fd.Left + fd.Width / 2, fd.Top + fd.Height / 2);
                log.AppendLine($"11d grip={gd} visible={grip11.Visibility} frame={fd}");
                Down(gd.X, gd.Y);
                double r0 = Math.Atan2(gd.Y - pd.Y, gd.X - pd.X), rr = (gd - pd).Length;
                for (int i = 1; i <= 9; i++)
                {
                    double a = r0 + (45.0 * Math.PI / 180) * i / 9;
                    Move(pd.X + rr * Math.Cos(a), pd.Y + rr * Math.Sin(a));
                }
                double a45 = r0 + 45.0 * Math.PI / 180;
                Up(pd.X + rr * Math.Cos(a45), pd.Y + rr * Math.Sin(a45));
                log.AppendLine($"11d after {BoxesIn(new Rect(250, -200, 400, 400))} (every top should be >= 0)");
                WindowShot("11d-group-rotate-edge.png");

                // 12. Survey's mark menu: icon row, Lock, Arrange with its submenu.
                Deselect();
                Tool(EditorTool.Select);
                InvokeN(editor, "Select", new object?[] { rect11a }); Pump(3);
                var markMenu = (ContextMenu)typeof(EditorWindow).GetMethod("BuildAnnotationMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
                void RenderEl(FrameworkElement el, string name)
                {
                    el.UpdateLayout();
                    if (el.ActualWidth < 1 || el.ActualHeight < 1) { log.AppendLine($"{name}: not laid out"); return; }
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth * 2), (int)Math.Ceiling(el.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 64, 72)), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                        dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    }
                    bmp.Render(dv);
                    Save(bmp, Path.Combine(outDir, name));
                }
                markMenu.PlacementTarget = viewport;
                markMenu.IsOpen = true; Pump(6);
                RenderEl(markMenu, "12a-mark-menu.png");
                var arrangeRow = FindByName(markMenu, "Arrange");
                log.AppendLine($"12 arrange row found={arrangeRow is not null}");
                arrangeRow?.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                Pump(6);
                var sub = (System.Windows.Controls.Primitives.Popup?)typeof(EditorWindow).GetField("_markArrangePopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);
                log.AppendLine($"12 submenu open={sub?.IsOpen}");
                if (sub?.Child is FrameworkElement subCard) RenderEl(subCard, "12b-arrange-submenu.png");
                markMenu.IsOpen = false; Pump(3);
                Invoke(editor, "ToggleSelectionLock", null); Pump(3);
                markMenu.IsOpen = true; Pump(6);
                RenderEl(markMenu, "12c-mark-menu-locked.png");
                markMenu.IsOpen = false; Pump(3);
                Invoke(editor, "ToggleSelectionLock", null); Pump(3);

                editor.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        File.WriteAllText(Path.Combine(outDir, "fixcheck.txt"), log + (failure?.ToString() ?? "ok"));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>The first element under <paramref name="root"/> whose automation name is <paramref name="name"/>.</summary>
    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && System.Windows.Automation.AutomationProperties.GetName(fe) == name) return fe;
            if (FindByName(child, name) is { } hit) return hit;
        }
        return null;
    }

    private static object? Invoke(EditorWindow editor, string name, object? arg)
    {
        var m = typeof(EditorWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == (arg is null ? 0 : 1));
        if (m is null) return null;
        return m.Invoke(editor, arg is null ? null : new[] { arg });
    }

    private static object? InvokeN(EditorWindow editor, string name, object?[] args)
    {
        var m = typeof(EditorWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(x => x.Name == name && x.GetParameters().Length == args.Length);
        return m.Invoke(editor, args);
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Pump(int n)
    {
        for (int i = 0; i < n; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
