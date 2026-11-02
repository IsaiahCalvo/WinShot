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
                void Down(double x, double y) { editor.PointerDown(P(x, y)); Pump(1); }
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

                editor.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        File.WriteAllText(Path.Combine(outDir, "fixcheck.txt"), failure?.ToString() ?? "ok");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
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
