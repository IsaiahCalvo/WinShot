using System.IO;
using System.Windows;
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
/// Renders the editor opened on a TALL image to a PNG (in-process RenderTargetBitmap, works over
/// RDP) so we can SEE whether a tall scroll-capture opens fitted-to-view or clipped to the top.
/// The synthetic image is RED at the very top and GREEN at the very bottom: if the render shows
/// both, the editor fitted the whole thing; if it shows only red, it's clipping. Gated behind
/// WINSHOT_RENDER_EDITOR=1 so it no-ops in normal runs (it creates a WPF Application).
/// </summary>
public class EditorRenderHarness
{
    [Fact]
    public void RenderTallImageEditorToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_EDITOR") != "1")
            return;

        string outDir = Path.Combine(Path.GetTempPath(), "winshot-editor-render");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) _ = new Application();
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);

                var bmp = new SD.Bitmap(253, 3075);
                using (var g = SD.Graphics.FromImage(bmp))
                {
                    g.Clear(SD.Color.RoyalBlue);
                    g.FillRectangle(SD.Brushes.Red, 0, 0, 253, 220);              // very top
                    g.FillRectangle(SD.Brushes.Lime, 0, 3075 - 220, 253, 220);    // very bottom
                }

                var editor = new EditorWindow(bmp, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                editor.Show();

                // Pump for the deferred fit + the async source-image load (BaseImage.Source).
                for (int i = 0; i < 40; i++) { Pump(); Thread.Sleep(25); }
                editor.UpdateLayout();
                Pump();

                RenderToPng(editor, Path.Combine(outDir, "editor-tall.png"));

                // Dump the real dimensions so we can see which value is truncating.
                var t = typeof(EditorWindow);
                double zoom = (double)t.GetField("_zoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
                var src = (SD.Bitmap)t.GetField("_source", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
                var baseTiles = (System.Windows.Controls.Panel)editor.FindName("BaseTiles");
                var canvasHost = (FrameworkElement)editor.FindName("CanvasHost");
                var viewport = (FrameworkElement)editor.FindName("Viewport");
                var editorSurface = (FrameworkElement)editor.FindName("EditorSurface");
                var viewScale = (System.Windows.Media.ScaleTransform)editor.FindName("ViewScale");
                double tilesH = 0; int tileCount = baseTiles.Children.Count;
                foreach (System.Windows.Controls.Image im in baseTiles.Children)
                    tilesH += (im.Source as BitmapSource)?.PixelHeight ?? 0;
                File.WriteAllText(Path.Combine(outDir, "dims.txt"),
                    $"_source={src.Width}x{src.Height}\n" +
                    $"_zoom={zoom}\n" +
                    $"BaseTiles count={tileCount} totalPixelHeight={tilesH}\n" +
                    $"BaseTiles.Width/Height={baseTiles.Width}x{baseTiles.Height} Actual={baseTiles.ActualWidth}x{baseTiles.ActualHeight}\n" +
                    $"CanvasHost.Actual={canvasHost.ActualWidth}x{canvasHost.ActualHeight}\n" +
                    $"EditorSurface.Actual={editorSurface.ActualWidth}x{editorSurface.ActualHeight} Render={editorSurface.RenderSize}\n" +
                    $"ViewScale={viewScale.ScaleX}x{viewScale.ScaleY}\n" +
                    $"Viewport.Actual={viewport.ActualWidth}x{viewport.ActualHeight}\n");

                // Force a fit-WHOLE view (small zoom, whole image centered) and render again, to
                // verify the ENTIRE tall image renders — the original bug only drew the top.
                var vt = (System.Windows.Media.TranslateTransform)editor.FindName("ViewTranslate");
                double fw = Math.Min(viewport.ActualWidth / 253.0, viewport.ActualHeight / 3075.0);
                viewScale.ScaleX = viewScale.ScaleY = fw;
                vt.X = (viewport.ActualWidth - 253 * fw) / 2;
                vt.Y = (viewport.ActualHeight - 3075 * fw) / 2;
                for (int i = 0; i < 4; i++) { Pump(); Thread.Sleep(25); }
                RenderToPng(editor, Path.Combine(outDir, "editor-fitwhole.png"));

                // Experiment: does the bitmap SCALING MODE cause the truncation at small zoom?
                foreach (var mode in new[] { BitmapScalingMode.Linear, BitmapScalingMode.LowQuality, BitmapScalingMode.NearestNeighbor })
                {
                    foreach (System.Windows.Controls.Image im in baseTiles.Children)
                        RenderOptions.SetBitmapScalingMode(im, mode);
                    for (int i = 0; i < 4; i++) { Pump(); Thread.Sleep(25); }
                    RenderToPng(editor, Path.Combine(outDir, $"editor-mode-{mode}.png"));
                }

                // Hypothesis: the ~589 SOURCE rows that survive == Viewport.ActualHeight, i.e.
                // a clip evaluated in pre-transform space. Toggle the suspects one at a time.
                var viewportPanel = (System.Windows.Controls.Panel)editor.FindName("Viewport");
                viewportPanel.ClipToBounds = false;
                for (int i = 0; i < 4; i++) { Pump(); Thread.Sleep(25); }
                RenderToPng(editor, Path.Combine(outDir, "editor-noclip-viewport.png"));
                viewportPanel.ClipToBounds = true;

                var annotation = (System.Windows.Controls.Panel)editor.FindName("AnnotationCanvas");
                var interaction = (System.Windows.Controls.Panel)editor.FindName("InteractionCanvas");
                bool a0 = annotation.ClipToBounds, i0 = interaction.ClipToBounds;
                annotation.ClipToBounds = false;
                interaction.ClipToBounds = false;
                for (int i = 0; i < 4; i++) { Pump(); Thread.Sleep(25); }
                RenderToPng(editor, Path.Combine(outDir, "editor-noclip-canvases.png"));
                annotation.ClipToBounds = a0;
                interaction.ClipToBounds = i0;

                editor.Close();
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
        Assert.True(Directory.GetFiles(outDir, "*.png").Length > 0, "No editor PNG produced.");
    }

    private static void RenderToPng(Window w, string path)
    {
        int width = (int)Math.Ceiling(w.ActualWidth);
        int height = (int)Math.Ceiling(w.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
