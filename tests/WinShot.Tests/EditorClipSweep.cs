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
/// Measures — no eyeballing — how many SOURCE rows of a tall image actually render in the
/// editor across image heights, zooms and window positions. The source is a gradient whose
/// green channel encodes row/16, so the deepest visible source row is recoverable from the
/// rendered pixels. Gated behind WINSHOT_RENDER_EDITOR=1.
/// </summary>
public class EditorClipSweep
{
    [Fact]
    public void SweepTallImageRendering()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_EDITOR") != "1")
            return;

        string outDir = Path.Combine(Path.GetTempPath(), "winshot-editor-render");
        Directory.CreateDirectory(outDir);
        var results = new System.Text.StringBuilder();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) _ = new Application();
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; // survive window closes
                ThemeResources.EnsureLoaded();
                var settings = new SettingsService();
                var history = new HistoryService(settings);

                foreach (int srcH in new[] { 800, 1500, 2500, 3075, 6000 })
                {
                    foreach (bool onScreen in new[] { false, true })
                    {
                        int deepest = MeasureDeepestVisibleRow(settings, history, srcH, onScreen, out double zoom, out double viewportH);
                        results.AppendLine($"srcH={srcH} onScreen={onScreen} zoom={zoom:0.0000} viewportH={viewportH:0.0} deepestVisibleSourceRow={deepest} (of {srcH})");
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        File.WriteAllText(Path.Combine(outDir, "sweep.txt"), results.ToString());
        Assert.Null(failure);
    }

    /// <summary>Renders the editor on a row-encoded gradient image and returns the deepest
    /// source row visible in the rendered output (0 = nothing rendered).</summary>
    private static int MeasureDeepestVisibleRow(SettingsService settings, HistoryService history,
        int srcH, bool onScreen, out double zoom, out double viewportH)
    {
        const int W = 253;
        var bmp = new SD.Bitmap(W, srcH);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            for (int y = 0; y < srcH; y += 4)
            {
                // Encode the row in G (row/16 mod 200 + 30) with R=200 marker rows every 128.
                int gv = 30 + (y / 16) % 200;
                using var b = new SD.SolidBrush(SD.Color.FromArgb(255, y % 128 < 8 ? 220 : 40, gv, 90));
                g.FillRectangle(b, 0, y, W, 4);
            }
        }

        var editor = new EditorWindow(bmp, settings, history)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = onScreen ? 100 : -6000,
            Top = onScreen ? 100 : -6000,
            ShowInTaskbar = false,
        };
        editor.Show();
        for (int i = 0; i < 40; i++) { Pump(); Thread.Sleep(20); }
        editor.UpdateLayout();
        Pump();

        var t = typeof(EditorWindow);
        zoom = (double)t.GetField("_zoom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
        var viewport = (FrameworkElement)editor.FindName("Viewport");
        viewportH = viewport.ActualHeight;

        int width = (int)Math.Ceiling(editor.ActualWidth);
        int height = (int)Math.Ceiling(editor.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(editor);
        var px = new byte[width * height * 4];
        rtb.CopyPixels(px, width * 4, 0);

        // Find the deepest rendered image pixel: scan the center column for our gradient
        // signature (B≈90) from the bottom up, then decode... decoding G is ambiguous mod 200,
        // so instead compute source row from the on-screen distance below the image's top edge.
        int centerX = width / 2;
        int topScreenY = -1, bottomScreenY = -1;
        for (int y = 0; y < height; y++)
        {
            int i = (y * width + centerX) * 4;
            byte b = px[i], g2 = px[i + 1], r = px[i + 2];
            // Gradient signature: B≈90, R saturated low/high, and NOT a gray (the editor
            // chrome is grayscale — R≈G≈B — which a bare B-test falsely matched).
            int spread = Math.Max(Math.Max(r, g2), b) - Math.Min(Math.Min(r, g2), b);
            bool isGradient = Math.Abs(b - 90) <= 20 && (r <= 70 || r >= 190) && spread >= 30 && px[i + 3] == 255;
            if (isGradient)
            {
                if (topScreenY < 0) topScreenY = y;
                bottomScreenY = y;
            }
        }

        int deepest = topScreenY < 0 ? 0 : (int)Math.Round((bottomScreenY - topScreenY + 1) / zoom);
        editor.Close();
        Pump();
        return deepest;
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
