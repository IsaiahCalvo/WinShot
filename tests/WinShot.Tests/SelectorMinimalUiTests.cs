using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

[Collection("MotionRefcount")]
public class SelectorMinimalUiTests
{
    [Theory]
    [InlineData(false, 1d)]
    [InlineData(false, 1.5d)]
    [InlineData(false, 2d)]
    [InlineData(true, 1d)]
    [InlineData(true, 1.5d)]
    [InlineData(true, 2d)]
    public void AltHidesTheMagnifierAndCoordinatesButKeepsTheCrosshair(bool allInOne, double scale)
    {
        RunSta(() =>
        {
            var previousScale = WinShot.Recording.RecordingMonitorDpi.ScaleSource;
            var previousAnimations = Motion.AnimationsEnabled;
            WinShot.Recording.RecordingMonitorDpi.ScaleSource = _ => scale;
            Motion.AnimationsEnabled = () => false;
            try
            {
                var monitor = new SD.Rectangle(0, 0, 640, 480);
                using WF.Form selector = allInOne
                    ? new FastAllInOneSelectorDialog(() => Task.FromResult(new List<WindowInfo>()), null)
                    : new FastRegionSelectorDialog(() => Task.FromResult(new List<WindowInfo>()), null);
                var frozen = new SD.Bitmap(monitor.Width, monitor.Height);
                using (var g = SD.Graphics.FromImage(frozen)) g.Clear(SD.Color.FromArgb(240, 240, 240));
                Set(selector, "_frozen", frozen);
                Set(selector, "_vs", monitor);
                Set(selector, "_monitorBounds", monitor);
                Set(selector, "_currentScreen", new SD.Point(80, 80));
                Set(selector, "_magnifierOn", true);
                Key(selector, true);
                using var hidden = Paint(selector, monitor);
                int ground = hidden.GetPixel(20, 20).ToArgb();
                Assert.NotEqual(ground, hidden.GetPixel(80, 300).ToArgb());
                for (int y = 100; y < 440; y++)
                    for (int x = 100; x < 580; x++)
                        Assert.Equal(ground, hidden.GetPixel(x, y).ToArgb());
                Key(selector, false);
                Key(selector, true);
                using var shown = Paint(selector, monitor);
                Assert.NotEqual(ground, shown.GetPixel(150, 180).ToArgb());
                Key(selector, false);
                Key(selector, true);
                Set(selector, "_dragging", true);
                Set(selector, "_dragMoved", true);
                Set(selector, "_dragStartScreen", new SD.Point(160, 120));
                Set(selector, "_currentScreen", new SD.Point(400, 300));
                using var selection = Paint(selector, monitor);
                int edgeRows = Enumerable.Range(115, 11).Count(y =>
                    selection.GetPixel(280, y).ToArgb() == ThemePalette.Accent.ToArgb());
                Assert.Equal(1, edgeRows);
                string? output = Environment.GetEnvironmentVariable("WINSHOT_MINIMAL_UI_OUT");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    string tag = $"{(allInOne ? "all-in-one" : "region")}-{scale}";
                    hidden.Save(Path.Combine(output, tag + "-hidden.png"));
                    shown.Save(Path.Combine(output, tag + "-shown.png"));
                    selection.Save(Path.Combine(output, tag + "-selection.png"));
                }
                selector.GetType().GetMethod("HandleKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(selector, new object[] { new WF.KeyEventArgs(WF.Keys.Escape) });
            }
            finally
            {
                Motion.AnimationsEnabled = previousAnimations;
                WinShot.Recording.RecordingMonitorDpi.ScaleSource = previousScale;
            }
        });
    }

    [Fact]
    public void MeasureFrozenMagnifierDrawingCost()
    {
        string? output = Environment.GetEnvironmentVariable("WINSHOT_BENCH_SELECTOR_OUT");
        if (string.IsNullOrEmpty(output)) return;
        bool busy = Environment.GetEnvironmentVariable("WINSHOT_BENCH_SELECTOR_BUSY") == "1";
        using var process = Process.GetCurrentProcess();
        nint affinity = process.ProcessorAffinity;
        using var stop = new CancellationTokenSource();
        Thread? load = null;
        if (busy)
        {
            // Compete on one core inside this test process, without filling the user's CPU.
            long mask = affinity.ToInt64();
            process.ProcessorAffinity = (nint)(mask & -mask);
            load = new Thread(() => { while (!stop.IsCancellationRequested) Thread.SpinWait(4096); });
            load.IsBackground = true;
            load.Start();
        }
        try
        {
            using var frozen = SelectorRenderHarness.SyntheticDesktop(1920, 1200);
            using var canvas = new SD.Bitmap(640, 480);
            using var g = SD.Graphics.FromImage(canvas);
            var screen = new SD.Rectangle(-1920, -1200, 1920, 1200);
            void Draw(int i) => FastSelectorLoupeRenderer.Draw(g, canvas.Size, screen,
                new SD.Point(80 + i % 200, 80), new SD.Point(-1840 + i % 200, -1120), frozen, scale: 1.5d);
            for (int i = 0; i < 10; i++) Draw(i);
            var runs = new List<object>();
            for (int run = 0; run < 5; run++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                int frames = 0;
                while (frames < 100 && clock.ElapsedMilliseconds < 3000) Draw(frames++);
                clock.Stop();
                runs.Add(new { milliseconds = clock.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before, frames });
            }
            File.WriteAllText(output, JsonSerializer.Serialize(runs));
            canvas.Save(output + ".png", SD.Imaging.ImageFormat.Png);
        }
        finally
        {
            stop.Cancel();
            load?.Join();
            if (busy) process.ProcessorAffinity = affinity;
        }
    }

    private static SD.Bitmap Paint(WF.Form selector, SD.Rectangle monitor)
    {
        var image = new SD.Bitmap(monitor.Width, monitor.Height);
        using var g = SD.Graphics.FromImage(image);
        selector.GetType().GetMethod("PaintSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(selector, new object[] { g, monitor });
        return image;
    }

    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Key(WF.Form selector, bool down) => selector.GetType()
        .GetMethod(down ? "HandleKeyDown" : "HandleKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(selector, new object[] { new WF.KeyEventArgs(down ? WF.Keys.Menu | WF.Keys.Alt : WF.Keys.Menu) });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
