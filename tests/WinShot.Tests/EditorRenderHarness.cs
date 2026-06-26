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
                var baseImage = (System.Windows.Controls.Image)editor.FindName("BaseImage");
                var canvasHost = (FrameworkElement)editor.FindName("CanvasHost");
                var viewport = (FrameworkElement)editor.FindName("Viewport");
                var editorSurface = (FrameworkElement)editor.FindName("EditorSurface");
                var viewScale = (System.Windows.Media.ScaleTransform)editor.FindName("ViewScale");
                var bs = baseImage.Source as BitmapSource;
                File.WriteAllText(Path.Combine(outDir, "dims.txt"),
                    $"_source={src.Width}x{src.Height}\n" +
                    $"_zoom={zoom}\n" +
                    $"BaseImage.Source=" + (bs is null ? "null" : $"{bs.PixelWidth}x{bs.PixelHeight} dpi={bs.DpiX}x{bs.DpiY} DIU={bs.Width}x{bs.Height}") + "\n" +
                    $"BaseImage.Width/Height={baseImage.Width}x{baseImage.Height} Actual={baseImage.ActualWidth}x{baseImage.ActualHeight} Stretch={baseImage.Stretch}\n" +
                    $"CanvasHost.Actual={canvasHost.ActualWidth}x{canvasHost.ActualHeight}\n" +
                    $"EditorSurface.Actual={editorSurface.ActualWidth}x{editorSurface.ActualHeight} Render={editorSurface.RenderSize}\n" +
                    $"ViewScale={viewScale.ScaleX}x{viewScale.ScaleY}\n" +
                    $"Viewport.Actual={viewport.ActualWidth}x{viewport.ActualHeight}\n");

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
