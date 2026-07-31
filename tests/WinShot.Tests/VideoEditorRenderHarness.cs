using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Recording;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Opt-in, sanitized visual evidence for the local video editor. The source path is an
/// empty temporary file and the preview is never played, so no user content is captured.
/// </summary>
public class VideoEditorRenderHarness
{
    [Fact]
    public void RenderVideoEditorToPng()
    {
        string? outDir = Environment.GetEnvironmentVariable("WINSHOT_VIDEO_EDITOR_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
            return;

        Directory.CreateDirectory(outDir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string tempVideo = Path.Combine(Path.GetTempPath(), $"winshot-video-editor-{Guid.NewGuid():N}.mp4");
            try
            {
                File.WriteAllBytes(tempVideo, Array.Empty<byte>());
                if (Application.Current is null)
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                Render(outDir, tempVideo, settings, history, 760, 600, "editor-wide.png");
                Render(outDir, tempVideo, settings, history, 640, 560, "editor-narrow.png");
                Render(outDir, tempVideo, settings, history, 760, 600, "editor-exporting.png", exporting: true);
                Render(outDir, tempVideo, settings, history, 760, 600, "editor-keyboard-focus.png", focusTrim: true);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try { File.Delete(tempVideo); }
                catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(outDir, "editor-wide.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "editor-narrow.png")));
    }

    private static void Render(
        string outDir,
        string tempVideo,
        SettingsService settings,
        HistoryService history,
        double width,
        double height,
        string fileName,
        bool exporting = false,
        bool focusTrim = false)
    {
        var window = new VideoEditorWindow(tempVideo, settings, history)
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            ShowInTaskbar = false,
        };
        window.Show();
        Pump();

        SetField(window, "_durationSec", 65d);
        SetField(window, "_trimInitialized", true);
        SetField(window, "_trimStartSec", 5d);
        SetField(window, "_trimEndSec", 58d);
        ((Slider)window.FindName("PositionSlider")!).Maximum = 65;
        ((Slider)window.FindName("PositionSlider")!).Value = 18;
        Invoke(window, "UpdateTrimUi");
        if (exporting)
        {
            Invoke(window, "SetExportUiState", true);
            ((ProgressBar)window.FindName("ExportProgress")!).Value = 42;
            ((TextBlock)window.FindName("StatusText")!).Text = "Exporting… 42%";
        }
        else
        {
            ((TextBlock)window.FindName("StatusText")!).Text = "Ready to export";
        }
        if (focusTrim)
        {
            var trimStart = (System.Windows.Controls.Primitives.Thumb)window.FindName("TrimStartThumb")!;
            trimStart.Focus();
            Keyboard.Focus(trimStart);
        }
        window.UpdateLayout();
        Pump();

        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(outDir, fileName));
        encoder.Save(output);
        window.Close();
        Pump();
    }

    private static void SetField(VideoEditorWindow window, string name, object value) =>
        typeof(VideoEditorWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Invoke(VideoEditorWindow window, string name, params object[] arguments) =>
        typeof(VideoEditorWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

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
