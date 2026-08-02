using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.SettingsUi;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Renders the Settings window (each tab) to PNG files via RenderTargetBitmap — an
/// in-process visual render, NOT a screen grab, so it works headless / over RDP. Lets us
/// SEE the actual rendered UI (text contrast, layout) and compare against the CleanShot
/// reference, instead of editing the XAML blind. Not an assertion test; produces artifacts
/// under %TEMP%\winshot-settings-render\.
/// </summary>
public class SettingsRenderHarness
{
    [Fact]
    public void RenderSettingsTabsToPng()
    {
        // Dev tool, not an assertion. It creates a WPF Application, which conflicts with the
        // one ThemedWindowTests creates, so it no-ops unless explicitly requested via
        // WINSHOT_RENDER=1 (run it alone: `WINSHOT_RENDER=1 dotnet test --filter RenderSettingsTabsToPng`).
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_DIR")
            ?? Path.Combine(Path.GetTempPath(), "winshot-settings-render");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.png"))
            File.Delete(f);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    _ = new Application();
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                settings.Current.SaveFolder = @"C:\Pictures\WinShot";

                var win = new SettingsWindow(settings)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    Opacity = 1,
                    ShowInTaskbar = false,
                };
                win.Show();
                Pump();

                var list = (ListBox)win.FindName("SectionList");
                SelectAndRender(win, list, 0, outDir, "normal-general.png");

                ((TextBox)win.FindName("SaveFolderBox")).Focus();
                Pump();
                win.UpdateLayout();
                RenderToPng(win, Path.Combine(outDir, "focus-general.png"), dpi: 96);

                // Same effective-pixel layout rasterized at 150% Windows display scaling.
                RenderToPng(win, Path.Combine(outDir, "high-dpi-general.png"), dpi: 144);

                SelectAndRender(win, list, 2, outDir, "quick-access.png");

                list.SelectedIndex = 3;
                Pump();
                var recordingTabs = (ListBox)win.FindName("RecordingSubList");
                SelectAndRender(win, recordingTabs, 0, outDir, "recording-general.png");
                SelectAndRender(win, recordingTabs, 1, outDir, "recording-video.png");
                SelectAndRender(win, recordingTabs, 2, outDir, "recording-gif.png");

                SelectAndRender(win, list, 4, outDir, "screenshots.png");
                SelectAndRender(win, list, 6, outDir, "advanced.png");

                win.Close();
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
        Assert.True(Directory.GetFiles(outDir, "*.png").Length > 0, "No tab PNGs were produced.");
    }

    private static void SelectAndRender(
        Window window,
        ListBox list,
        int index,
        string outDir,
        string fileName)
    {
        list.SelectedIndex = index;
        Pump();
        window.UpdateLayout();
        RenderToPng(window, Path.Combine(outDir, fileName), dpi: 96);
    }

    private static void RenderToPng(Window w, string path, double dpi)
    {
        double scale = dpi / 96d;
        int width = (int)Math.Ceiling(w.ActualWidth * scale);
        int height = (int)Math.Ceiling(w.ActualHeight * scale);
        if (width <= 0 || height <= 0) return;

        var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
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
