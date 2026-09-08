using System.Diagnostics;
using System.Drawing;
using System.IO;
using WinShot.Core;
using WinShot.History;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class HistoryPreviewLayoutTests
{
    [Fact]
    public void CalculateImageSize_DownscalesLargeImageToWorkAreaLimit()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(4000, 2000),
            new Size(1000, 800));

        Assert.Equal(new Size(816, 416), size);
    }

    [Fact]
    public void CalculateImageSize_KeepsSmallImagesAtNativeSizeWithPadding()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(200, 100),
            new Size(1000, 800));

        Assert.Equal(new Size(216, 116), size);
    }

    [Fact]
    public void CalculateImageSize_UsesMinimumContentSizeForTinyImages()
    {
        var size = HistoryPreviewLayout.CalculateImageSize(
            new Size(1, 1),
            new Size(1000, 800));

        Assert.Equal(new Size(64, 64), size);
    }

    [Fact]
    public void CalculateFallbackSize_IsStable()
    {
        Assert.Equal(new Size(360, 128), HistoryPreviewLayout.FallbackSize);
    }

    [Fact]
    public void CalculateImageSize_ScalesItsChromeWithTheMonitor()
    {
        // The image is physical pixels either way; only the 8 px inset around it scales.
        Assert.Equal(
            new Size(224, 124),
            HistoryPreviewLayout.CalculateImageSize(new Size(200, 100), new Size(1000, 800), 24));
    }
}

/// <summary>
/// Quick Look's entrance and exit. Two things break here and nothing watched either: a
/// machine with animations off must still SEE the window (it is born at Opacity 0), and a
/// close request must actually terminate rather than parking the form in the cancelled
/// state its exit fade puts it in.
/// </summary>
public class QuickPreviewMotionTests
{
    private sealed class TempPng : IDisposable
    {
        public string Path { get; }

        public TempPng()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"winshot-quicklook-{Guid.NewGuid():N}.png");
            using var bmp = new Bitmap(120, 80);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.SlateGray);
            bmp.Save(Path, System.Drawing.Imaging.ImageFormat.Png);
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    private static void PumpFor(int ms)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Exception(error.Message, error);
    }

    [Fact]
    public void WithAnimationsOffTheWindowIsVisibleImmediately()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            try
            {
                using var file = new TempPng();
                using var preview = new FastQuickPreviewWindow(file.Path);
                preview.Show();
                PumpFor(120);
                Assert.Equal(1d, preview.Opacity, 3);
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }

    [Fact]
    public void CloseNowEndsTheWindowInTheSameCallSoAReplacementNeverOverlapsIt()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                using var file = new TempPng();
                var preview = new FastQuickPreviewWindow(file.Path);
                preview.Show();
                PumpFor(60);

                preview.CloseNow();

                // History steps Left/Right by rebuilding the window; the outgoing one has to
                // be gone before the incoming one is shown, not 220 ms of fade later.
                Assert.True(preview.IsDisposed, "the outgoing preview is still on screen");
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }

    [Fact]
    public void AnOrdinaryCloseStillTerminatesAfterItsExitFade()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                using var file = new TempPng();
                var preview = new FastQuickPreviewWindow(file.Path);
                preview.Show();
                PumpFor(60);

                preview.Close();
                PumpFor(Motion.SwapMs + 250);

                Assert.True(preview.IsDisposed, "the exit fade parked the form in the cancelled state");
                Assert.Equal(0, Motion.Holders);
            }
            finally { Motion.AnimationsEnabled = previous; }
        });
    }
}
