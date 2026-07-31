using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

                var filledRectangle = (RadioButton)editor.FindName("FilledRectangleToolBtn")!;
                filledRectangle.IsChecked = true;
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-filled-rectangle-context.png"));

                var moreTools = (System.Windows.Controls.Primitives.Popup)editor.FindName("MoreToolsPopup")!;
                moreTools.IsOpen = true;
                Pump(4);
                RenderElementToPng(moreTools.Child, Path.Combine(outDir, "editor-after-more-tools-overflow.png"));
                moreTools.IsOpen = false;

                UIElement selectedRectangle = AddRepresentativeAnnotations(canvas);
                Pump(6);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-annotations-overview.png"));
                RenderToPng(editor, Path.Combine(outDir, "editor-after-shell-200pct.png"), dpi: 192);

                SelectTool(editor, EditorTool.Rectangle);
                InvokePrivate(editor, "Select", selectedRectangle);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-rectangle-selection-context.png"));

                InvokePrivate(editor, "Select", null);
                SelectTool(editor, EditorTool.Blur);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-blur-strength-context.png"));

                SelectTool(editor, EditorTool.Text);
                Pump(4);
                RenderToPng(editor, Path.Combine(outDir, "editor-after-text-style-context.png"));

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
                    "Renders: primary shell, filled rectangle context, More overflow, annotations,\n" +
                    "200% DPI, rectangle selection, blur strength, text style, crop ratio, and spotlight.\n");

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
        });

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
