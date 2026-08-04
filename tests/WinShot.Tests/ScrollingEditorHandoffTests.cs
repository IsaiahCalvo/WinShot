using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class ScrollingEditorHandoffTests
{
    private const int ImageWidth = 384;
    private const int ImageHeight = 6144;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void DirectAndHistoryEdit_RenderTallCaptureOffScreen_AndRemainEditable()
    {
        // WPF permits only one Application per test process. Keep this real-window render
        // harness opt-in (like the other render harnesses) so the normal full suite's shared
        // themed-window smoke test remains order-independent.
        if (Environment.GetEnvironmentVariable("WINSHOT_RUN_SCROLLING_EDITOR_HANDOFF") != "1")
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
            Application? app = null;
            try
            {
                app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings, () => tempDir);
                SD.Bitmap directSource = CreateTallScrollingBitmap();

                string historyPath;
                using (var historyCopy = CaptureService.CloneBitmap(directSource))
                    historyPath = history.Add(historyCopy, HistoryCaptureKind.Scrolling);

                ValidateEditor(directSource, settings, history, "direct-edit.png");

                SD.Bitmap historySource;
                using (var fromDisk = new SD.Bitmap(historyPath))
                    historySource = new SD.Bitmap(fromDisk);
                ValidateEditor(historySource, settings, history, "history-edit.png");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try { app?.Shutdown(); } catch { }
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Off-screen editor test timed out.");
        Assert.Null(failure);
    }

    private static void ValidateEditor(
        SD.Bitmap source,
        SettingsService settings,
        HistoryService history,
        string evidenceFileName)
    {
        var editor = EditorWindow.CreateForCapture(source, settings, history);
        try
        {
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Left = -10000;
            editor.Top = -10000;
            editor.ShowInTaskbar = false;
            editor.ShowActivated = false;
            editor.Show();

            FieldInfo activeField = typeof(EditorWindow).GetField("_sourceOperationActive", PrivateInstance)!;
            WaitUntil(() => !(bool)activeField.GetValue(editor)!, TimeSpan.FromSeconds(10));
            editor.UpdateLayout();
            PumpDispatcherOnce();

            var tiles = (Panel)editor.FindName("BaseTiles");
            Assert.Equal(3, tiles.Children.Count);
            Assert.Equal(ImageHeight, tiles.Children.Cast<System.Windows.Controls.Image>()
                .Sum(image => ((BitmapSource)image.Source).PixelHeight));
            Assert.All(tiles.Children.Cast<System.Windows.Controls.Image>(),
                image => Assert.True(((BitmapSource)image.Source).IsFrozen));

            SD.Color before = source.GetPixel(31, 31);
            MethodInfo applyBlur = typeof(EditorWindow).GetMethod("ApplyBlur", PrivateInstance)!;
            applyBlur.Invoke(editor, [new SD.Rectangle(8, 8, 96, 96)]);
            WaitUntil(() => !(bool)activeField.GetValue(editor)!, TimeSpan.FromSeconds(10));

            SD.Color after = source.GetPixel(31, 31);
            Assert.NotEqual(before.ToArgb(), after.ToArgb());
            object undoStack = typeof(EditorWindow).GetField("_undoStack", PrivateInstance)!.GetValue(editor)!;
            Assert.Equal(1, (int)undoStack.GetType().GetProperty("Count")!.GetValue(undoStack)!);

            RenderTargetBitmap render = RenderEditor(editor);
            AssertContainsCaptureBands(render);

            string? evidenceDir = Environment.GetEnvironmentVariable("WINSHOT_SCROLLING_EDITOR_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidenceDir))
            {
                Directory.CreateDirectory(evidenceDir);
                SavePng(render, Path.Combine(evidenceDir, evidenceFileName));
            }
        }
        finally
        {
            editor.Close();
            PumpDispatcherOnce();
        }
    }

    private static SD.Bitmap CreateTallScrollingBitmap()
    {
        var bitmap = new SD.Bitmap(ImageWidth, ImageHeight, SD.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.RoyalBlue);
        graphics.FillRectangle(SD.Brushes.Red, 0, 0, ImageWidth, 180);
        graphics.FillRectangle(SD.Brushes.Lime, 0, ImageHeight - 180, ImageWidth, 180);
        for (int y = 8; y < 104; y += 8)
        {
            for (int x = 8; x < 104; x += 8)
            {
                graphics.FillRectangle(((x + y) / 8) % 2 == 0 ? SD.Brushes.Black : SD.Brushes.White,
                    x, y, 8, 8);
            }
        }
        return bitmap;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(10);
        }
        Assert.True(condition(), "Editor source operation did not complete.");
    }

    private static RenderTargetBitmap RenderEditor(Window window)
    {
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        Assert.True(width > 0 && height > 0, "Editor window was not laid out.");

        var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        render.Render(window);
        return render;
    }

    private static void AssertContainsCaptureBands(BitmapSource render)
    {
        int stride = render.PixelWidth * 4;
        var pixels = new byte[stride * render.PixelHeight];
        render.CopyPixels(pixels, stride, 0);

        bool hasRed = false;
        bool hasGreen = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte blue = pixels[i];
            byte green = pixels[i + 1];
            byte red = pixels[i + 2];
            hasRed |= red > 180 && green < 100 && blue < 100;
            hasGreen |= green > 180 && red < 100 && blue < 100;
            if (hasRed && hasGreen) break;
        }

        Assert.True(hasRed, "Rendered editor preview did not contain the capture's red top band.");
        Assert.True(hasGreen, "Rendered editor preview did not contain the capture's green bottom band.");
    }

    private static void SavePng(BitmapSource render, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpDispatcherOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
