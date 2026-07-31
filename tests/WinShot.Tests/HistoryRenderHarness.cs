using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class HistoryRenderHarness
{
    [Fact]
    public void RenderSanitizedHistoryToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_HISTORY_RENDER") != "1")
            return;

        string outputDir = Environment.GetEnvironmentVariable("WINSHOT_HISTORY_RENDER_DIR")
            ?? Path.Combine(Path.GetTempPath(), "winshot-history-render");
        int fixtureCount = int.TryParse(
            Environment.GetEnvironmentVariable("WINSHOT_HISTORY_RENDER_COUNT"),
            out int requestedCount)
            ? Math.Clamp(requestedCount, 1, 5_000)
            : 4;
        string outputPrefix = Environment.GetEnvironmentVariable("WINSHOT_HISTORY_RENDER_PREFIX")
            ?? "history";
        string historyDir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(historyDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ThemeResources.EnsureLoaded();
                WriteFixtures(historyDir, fixtureCount);

                var settings = new SettingsService();
                settings.Current.HistoryLimit = fixtureCount;
                var service = new HistoryService(settings, () => historyDir);
                var window = new HistoryWindow(service, settings)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000,
                    Top = -6000,
                    ShowInTaskbar = false,
                };
                window.Show();
                Pump();
                InvokeReload(window);
                int expectedVisible = Math.Min(fixtureCount, HistoryIncrementalCollection.DefaultPageSize);
                PumpUntil(() => ((ListBox)window.FindName("ItemsList")).Items.Count == expectedVisible);
                window.UpdateLayout();
                Render(window, Path.Combine(outputDir, $"{outputPrefix}-idle.png"));

                var list = (ListBox)window.FindName("ItemsList");
                list.SelectedIndex = 0;
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
                    first.Focus();
                Pump();
                window.UpdateLayout();
                Render(window, Path.Combine(outputDir, $"{outputPrefix}-keyboard-focus.png"));

                if (fixtureCount > HistoryIncrementalCollection.DefaultPageSize)
                {
                    var loadMore = (Button)window.FindName("LoadMoreButton");
                    loadMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    int expectedAfterLoad = Math.Min(
                        fixtureCount,
                        HistoryIncrementalCollection.DefaultPageSize * 2);
                    PumpUntil(() => list.Items.Count == expectedAfterLoad);
                    window.UpdateLayout();
                    Render(window, Path.Combine(outputDir, $"{outputPrefix}-after-load-more.png"));
                }

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        DeleteTempDir(historyDir);
        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(outputDir, $"{outputPrefix}-idle.png")));
        Assert.True(File.Exists(Path.Combine(outputDir, $"{outputPrefix}-keyboard-focus.png")));
        if (fixtureCount > HistoryIncrementalCollection.DefaultPageSize)
            Assert.True(File.Exists(Path.Combine(outputDir, $"{outputPrefix}-after-load-more.png")));
    }

    private static void WriteFixtures(string dir, int count)
    {
        if (count > 4)
        {
            DateTime start = DateTime.Today.AddHours(16);
            for (int index = 0; index < count; index++)
            {
                string extension = index % 12 == 0 ? ".gif" : ".mp4";
                string name = $"{start.AddMinutes(-30 * index):yyyyMMdd-HHmmss-fff}{extension}";
                File.WriteAllText(Path.Combine(dir, name), "sanitized local media fixture");
            }
            return;
        }

        if (count >= 1)
            WriteImage(Path.Combine(dir, "20260731-153000-000.png"), SD.Color.FromArgb(44, 122, 229), SD.Color.White);
        if (count >= 2)
            WriteImage(Path.Combine(dir, "20260731-141500-000.jpg"), SD.Color.FromArgb(33, 37, 45), SD.Color.CornflowerBlue);
        if (count >= 3)
            File.WriteAllText(Path.Combine(dir, "20260730-163000-000.mp4"), "sanitized video fixture");
        if (count >= 4)
            File.WriteAllText(Path.Combine(dir, "20260729-110000-000.gif"), "sanitized gif fixture");
    }

    private static void WriteImage(string path, SD.Color background, SD.Color foreground)
    {
        using var bitmap = new SD.Bitmap(640, 400);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(background);
        using var pen = new SD.Pen(foreground, 18);
        graphics.DrawRectangle(pen, 92, 72, 456, 256);
        graphics.DrawLine(pen, 120, 290, 500, 110);
        bitmap.Save(path, Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            ? ImageFormat.Jpeg
            : ImageFormat.Png);
    }

    private static void InvokeReload(HistoryWindow window)
    {
        var method = typeof(HistoryWindow).GetMethod("ReloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(window, null)!;
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Render(Window window, string path)
    {
        int width = (int)Math.Ceiling(window.ActualWidth);
        int height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "Timed out waiting for the History render state.");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
