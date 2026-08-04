using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private const int ImageWidth = 1024;
    private const int ImageHeight = 16384;
    private const int TileHeight = 2048;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void HandleCapture_DirectEdit_PreviewsBeforeConcurrentPostWork_AndRemainsEditable()
    {
        // WPF permits only one Application per process. Keep this real App.HandleCapture harness
        // opt-in, like the other render harnesses, so the normal suite remains order-independent.
        if (Environment.GetEnvironmentVariable("WINSHOT_RUN_SCROLLING_EDITOR_HANDOFF") != "1")
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "WinShotTests", Guid.NewGuid().ToString("N"));
            WinShot.App? app = null;
            EditorWindow? directEditor = null;
            EditorWindow? historyEditor = null;
            SD.Bitmap? source = null;
            bool editorOwnsSource = false;
            using var sampleCancellation = new CancellationTokenSource();
            Thread? sampler = null;
            try
            {
                TraceStep("STA harness started");
                app = new OffScreenTestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ThemeResources.EnsureLoaded();

                SettingsService settings = app.SettingsForTest;
                settings.Current.PostCaptureAction = PostCaptureAction.Edit;
                settings.Current.AutoCopyToClipboard = true;
                settings.Current.DownscaleHiDpi = false;
                var history = new HistoryService(settings, () => tempDir);
                app.SetHistoryForTest(history);

                CollectGarbage();
                long privateBeforeSource = ReadPrivateMemory();
                long allocatedBeforeSource = GC.GetTotalAllocatedBytes(precise: true);
                source = CreateTallScrollingBitmap();
                TraceStep("64 MiB source created");
                long privateWithSource = ReadPrivateMemory();
                long peakPrivate = privateWithSource;
                sampler = StartMemorySampler(sampleCancellation.Token, value => AtomicMax(ref peakPrivate, value));

                int activeBorrowers = 0;
                int maximumBorrowers = 0;
                int historyCalls = 0;
                int autoCopyCalls = 0;
                int previewReadyBeforeHistory = 0;
                int previewReadyBeforeAutoCopy = 0;
                Exception? historyFailure = null;
                Exception? autoCopyFailure = null;
                int syntheticDibChecksum = 0;

                app.DirectEditTestHooks = new DirectEditCaptureTestHooks
                {
                    ConfigureEditor = editor =>
                    {
                        ConfigureOffScreen(editor);
                        editor.Closed += (_, _) => TraceStep("Direct editor Closed event fired");
                    },
                    EditorShown = editor => directEditor = editor,
                    HistoryWork = async borrowed =>
                    {
                        Interlocked.Increment(ref historyCalls);
                        if (directEditor?.InitialPreviewReady.IsCompleted == true)
                            Interlocked.Exchange(ref previewReadyBeforeHistory, 1);
                        EnterBorrower(ref activeBorrowers, ref maximumBorrowers);
                        try
                        {
                            await Task.Run(() => history.Add(borrowed, HistoryCaptureKind.Scrolling));
                        }
                        catch (Exception ex)
                        {
                            historyFailure = ex;
                            throw;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeBorrowers);
                        }
                    },
                    AutoCopyWork = async borrowed =>
                    {
                        Interlocked.Increment(ref autoCopyCalls);
                        if (directEditor?.InitialPreviewReady.IsCompleted == true)
                            Interlocked.Exchange(ref previewReadyBeforeAutoCopy, 1);
                        EnterBorrower(ref activeBorrowers, ref maximumBorrowers);
                        try
                        {
                            syntheticDibChecksum = await Task.Run(() => CopyToSyntheticDib(borrowed));
                        }
                        catch (Exception ex)
                        {
                            autoCopyFailure = ex;
                            throw;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeBorrowers);
                        }
                    },
                };

                var elapsed = Stopwatch.StartNew();
                app.HandleCaptureForTest(source, PostCaptureAction.Edit, HistoryCaptureKind.Scrolling);
                editorOwnsSource = true;
                long handleCaptureReturnMs = elapsed.ElapsedMilliseconds;
                TraceStep($"HandleCapture returned in {handleCaptureReturnMs} ms");
                Assert.NotNull(directEditor);

                WaitForTask(directEditor.InitialPreviewReady, TimeSpan.FromSeconds(30));
                long previewReadyMs = elapsed.ElapsedMilliseconds;
                TraceStep($"Direct preview ready in {previewReadyMs} ms");
                bool dispatcherRespondedDuringPostWork = PumpDispatcherOnce();
                WaitForTask(directEditor.InitialCaptureReady, TimeSpan.FromSeconds(45));
                long allWorkReadyMs = elapsed.ElapsedMilliseconds;
                TraceStep($"Post-work ready in {allWorkReadyMs} ms");

                sampleCancellation.Cancel();
                Assert.True(sampler.Join(TimeSpan.FromSeconds(2)), "Memory sampler did not stop.");
                sampler = null;
                long allocatedThroughHandoff = GC.GetTotalAllocatedBytes(precise: true) - allocatedBeforeSource;

                Assert.True(handleCaptureReturnMs < 2000,
                    $"HandleCapture blocked the UI for {handleCaptureReturnMs} ms.");
                Assert.True(previewReadyMs < 5000,
                    $"The 64 MiB scrolling preview took {previewReadyMs} ms to appear.");
                Assert.True(allWorkReadyMs < 10000,
                    $"The 64 MiB direct Edit handoff took {allWorkReadyMs} ms to become editable.");
                Assert.True(dispatcherRespondedDuringPostWork, "The UI dispatcher did not respond during post-capture work.");
                Assert.Equal(1, historyCalls);
                Assert.Equal(1, autoCopyCalls);
                Assert.Equal(1, previewReadyBeforeHistory);
                Assert.Equal(1, previewReadyBeforeAutoCopy);
                Assert.Equal(1, maximumBorrowers);
                Assert.Null(historyFailure);
                Assert.Null(autoCopyFailure);
                Assert.NotEqual(0, syntheticDibChecksum);

                ValidateEditorTiles(directEditor);
                bool editorClosed = (bool)typeof(EditorWindow).GetField("_closed", PrivateInstance)!.GetValue(directEditor)!;
                TraceStep($"Before edit validation: IsVisible={directEditor.IsVisible}, closed={editorClosed}");
                Assert.True(directEditor.IsVisible);
                Assert.False(editorClosed);
                ValidateEditable(directEditor, source);
                RenderTargetBitmap directRender = RenderEditor(directEditor);
                AssertContainsCaptureBands(directRender);
                SaveEvidence(directRender, "direct-edit-actual-handle-capture.png");
                TraceStep("Direct editor validated and rendered");

                string historyPath = Assert.Single(Directory.GetFiles(tempDir, "*-scroll.png"));
                directEditor.Close();
                directEditor = null;
                PumpDispatcherOnce();

                var historySource = new SD.Bitmap(historyPath);
                historyEditor = EditorWindow.CreateForCapture(historySource, settings, history);
                ConfigureOffScreen(historyEditor);
                historyEditor.Show();
                WaitUntilEditorReady(historyEditor, TimeSpan.FromSeconds(30));
                TraceStep("History editor ready");
                ValidateEditorTiles(historyEditor);
                RenderTargetBitmap historyRender = RenderEditor(historyEditor);
                AssertContainsCaptureBands(historyRender);
                SaveEvidence(historyRender, "history-edit.png");

                WriteMeasurements(
                    privateBeforeSource,
                    privateWithSource,
                    peakPrivate,
                    allocatedThroughHandoff,
                    handleCaptureReturnMs,
                    previewReadyMs,
                    allWorkReadyMs,
                    dispatcherRespondedDuringPostWork);
                TraceStep("Measurements written");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                sampleCancellation.Cancel();
                try { sampler?.Join(TimeSpan.FromSeconds(2)); } catch { }
                try { historyEditor?.Close(); } catch { }
                try { directEditor?.Close(); } catch { }
                if (!editorOwnsSource)
                    source?.Dispose();
                try { PumpDispatcherOnce(); } catch { }
                try { app?.Shutdown(); } catch { }
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Off-screen App.HandleCapture test timed out.");
        Assert.Null(failure);
    }

    private static void ConfigureOffScreen(EditorWindow editor)
    {
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = -10000;
        editor.Top = -10000;
        editor.ShowInTaskbar = false;
        editor.ShowActivated = false;
    }

    private static void ValidateEditorTiles(EditorWindow editor)
    {
        editor.UpdateLayout();
        PumpDispatcherOnce();
        var tiles = (Panel)editor.FindName("BaseTiles");
        Assert.Equal(ImageHeight / TileHeight, tiles.Children.Count);
        Assert.Equal(ImageHeight, tiles.Children.Cast<System.Windows.Controls.Image>()
            .Sum(image => ((BitmapSource)image.Source).PixelHeight));
        Assert.All(tiles.Children.Cast<System.Windows.Controls.Image>(),
            image => Assert.True(((BitmapSource)image.Source).IsFrozen));
    }

    private static void ValidateEditable(EditorWindow editor, SD.Bitmap source)
    {
        FieldInfo activeField = typeof(EditorWindow).GetField("_sourceOperationActive", PrivateInstance)!;
        Assert.False((bool)activeField.GetValue(editor)!);

        SD.Color before = source.GetPixel(31, 31);
        MethodInfo applyBlur = typeof(EditorWindow).GetMethod("ApplyBlur", PrivateInstance)!;
        applyBlur.Invoke(editor, [new SD.Rectangle(8, 8, 96, 96)]);
        WaitUntil(() => !(bool)activeField.GetValue(editor)!, TimeSpan.FromSeconds(10));

        SD.Color after = source.GetPixel(31, 31);
        Assert.NotEqual(before.ToArgb(), after.ToArgb());
        object undoStack = typeof(EditorWindow).GetField("_undoStack", PrivateInstance)!.GetValue(editor)!;
        Assert.Equal(1, (int)undoStack.GetType().GetProperty("Count")!.GetValue(undoStack)!);
    }

    private static SD.Bitmap CreateTallScrollingBitmap()
    {
        var bitmap = new SD.Bitmap(ImageWidth, ImageHeight, SD.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(SD.Color.RoyalBlue);
        graphics.FillRectangle(SD.Brushes.Red, 0, 0, ImageWidth, 320);
        graphics.FillRectangle(SD.Brushes.Lime, 0, ImageHeight - 320, ImageWidth, 320);
        for (int y = 512; y < ImageHeight - 512; y += 512)
            graphics.FillRectangle(y % 1024 == 0 ? SD.Brushes.Gold : SD.Brushes.Navy, 0, y, ImageWidth, 32);
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

    private static int CopyToSyntheticDib(SD.Bitmap bitmap)
    {
        var rect = new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        SD.Imaging.BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(rect, SD.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);
            int rowBytes = checked(bitmap.Width * 4);
            var dib = GC.AllocateUninitializedArray<byte>(checked(rowBytes * bitmap.Height));
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr row = data.Stride >= 0
                    ? IntPtr.Add(data.Scan0, y * data.Stride)
                    : IntPtr.Add(data.Scan0, (bitmap.Height - 1 - y) * -data.Stride);
                Marshal.Copy(row, dib, y * rowBytes, rowBytes);
            }

            Thread.Sleep(75); // Keep the source-sized clipboard payload visible to the sampler.
            return dib[0] ^ dib[dib.Length / 2] ^ dib[^1] ^ dib.Length;
        }
        finally
        {
            if (data is not null)
                bitmap.UnlockBits(data);
        }
    }

    private static Thread StartMemorySampler(CancellationToken token, Action<long> sample)
    {
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                sample(ReadPrivateMemory());
                Thread.Sleep(10);
            }
        })
        {
            IsBackground = true,
            Name = "WinShot test memory sampler",
        };
        thread.Start();
        return thread;
    }

    private static long ReadPrivateMemory()
    {
        using Process process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void EnterBorrower(ref int activeBorrowers, ref int maximumBorrowers)
    {
        int active = Interlocked.Increment(ref activeBorrowers);
        AtomicMax(ref maximumBorrowers, active);
    }

    private static void AtomicMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void AtomicMax(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void WaitForTask(Task task, TimeSpan timeout)
    {
        WaitUntil(() => task.IsCompleted, timeout);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntilEditorReady(EditorWindow editor, TimeSpan timeout)
    {
        FieldInfo activeField = typeof(EditorWindow).GetField("_sourceOperationActive", PrivateInstance)!;
        WaitUntil(() => !(bool)activeField.GetValue(editor)!, timeout);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcherOnce();
            Thread.Sleep(5);
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

    private static void SaveEvidence(BitmapSource render, string fileName)
    {
        string? evidenceDir = Environment.GetEnvironmentVariable("WINSHOT_SCROLLING_EDITOR_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDir))
            return;

        Directory.CreateDirectory(evidenceDir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var stream = File.Create(Path.Combine(evidenceDir, fileName));
        encoder.Save(stream);
    }

    private static void TraceStep(string message)
    {
        string? evidenceDir = Environment.GetEnvironmentVariable("WINSHOT_SCROLLING_EDITOR_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDir))
            return;

        Directory.CreateDirectory(evidenceDir);
        File.AppendAllText(
            Path.Combine(evidenceDir, "progress.txt"),
            $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
    }

    private static void WriteMeasurements(
        long privateBeforeSource,
        long privateWithSource,
        long peakPrivate,
        long allocatedThroughHandoff,
        long handleCaptureReturnMs,
        long previewReadyMs,
        long allWorkReadyMs,
        bool dispatcherResponded)
    {
        string? evidenceDir = Environment.GetEnvironmentVariable("WINSHOT_SCROLLING_EDITOR_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDir))
            return;

        Directory.CreateDirectory(evidenceDir);
        EditorCaptureMemoryBudget measuredBudget = CaptureService.EstimateEditorCaptureMemory(
            ImageWidth, ImageHeight, TileHeight);
        EditorCaptureMemoryBudget limitBudget = CaptureService.EstimateEditorCaptureMemory(
            4096, 32768, TileHeight);
        string[] lines =
        [
            "Path: App.HandleCaptureForTest -> private App.HandleCapture -> direct Edit -> EditorWindow.CreateForDirectCapture",
            "Post-work: real HistoryService.Add plus synthetic source-sized auto-copy DIB; both use the production ownership gate",
            $"Fixture: {ImageWidth}x{ImageHeight} BGRA32 ({MiB(measuredBudget.SourceBytes):F1} MiB source)",
            $"HandleCapture return: {handleCaptureReturnMs} ms",
            $"Initial preview ready: {previewReadyMs} ms",
            $"Preview + History + auto-copy ready: {allWorkReadyMs} ms",
            $"UI dispatcher responsive during post-work: {dispatcherResponded}",
            $"Private bytes before source: {MiB(privateBeforeSource):F1} MiB",
            $"Private bytes with source: {MiB(privateWithSource):F1} MiB",
            $"Peak private bytes: {MiB(peakPrivate):F1} MiB",
            $"Peak private delta from before source: {MiB(peakPrivate - privateBeforeSource):F1} MiB",
            $"Managed allocations through handoff: {MiB(allocatedThroughHandoff):F1} MiB",
            $"Full-resolution GDI clones in direct handoff: {measuredBudget.FullResolutionCloneCount}",
            $"64 MiB fixture pixel budget without auto-copy: {MiB(measuredBudget.PeakPixelBytesWithoutAutoCopy):F1} MiB",
            $"64 MiB fixture pixel budget with auto-copy: {MiB(measuredBudget.PeakPixelBytesWithAutoCopy):F1} MiB",
            $"512 MiB limit pixel budget without auto-copy: {MiB(limitBudget.PeakPixelBytesWithoutAutoCopy):F1} MiB",
            $"512 MiB limit pixel budget with auto-copy: {MiB(limitBudget.PeakPixelBytesWithAutoCopy):F1} MiB",
            "Budget excludes PNG encoder, WPF object, and process baseline overhead.",
        ];
        File.WriteAllLines(Path.Combine(evidenceDir, "measurements.txt"), lines);
    }

    private static double MiB(long bytes) => bytes / 1024d / 1024d;

    private sealed class OffScreenTestApp : WinShot.App
    {
        // The harness needs the real App.HandleCapture implementation, but starting the real
        // tray, hotkeys, command server, and single-instance coordination would touch the user's
        // desktop/runtime. Suppress only lifecycle side effects; capture routing is inherited.
        protected override void OnStartup(StartupEventArgs e)
        {
        }

        protected override void OnExit(ExitEventArgs e)
        {
        }
    }

    private static bool PumpDispatcherOnce()
    {
        bool responded = false;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                responded = true;
                frame.Continue = false;
            }));
        Dispatcher.PushFrame(frame);
        return responded;
    }
}
