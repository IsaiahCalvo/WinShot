using System.Diagnostics;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Round-7 breaker guards, batch B: disposing a surface mid-operation, churning every new
/// window through its entrance, backdrop padding at every size, nudging on a negative-Y
/// monitor, and the auto format choice for a transparent capture.
/// </summary>
public class BreakerProbesRound7B
{
    private const BindingFlags NonPub = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Call(object t, string name, params object?[] args) =>
        t.GetType().GetMethod(name, NonPub)!.Invoke(t, args);

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception(error.Message, error);
    }

    private static SD.Bitmap Solid(int w, int h, SD.Color c)
    {
        var b = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(c);
        return b;
    }

    // ------------------------------------------------------------------
    // I. The card is disposed while an async pixel edit is still awaiting
    //    the background thumbnail render (WaitForRenderIdleAsync).
    // ------------------------------------------------------------------

    [Fact]
    public void I_DisposingTheCardMidRotateLeavesNothingRunning()
    {
        RunSta(() =>
        {
            var card = new FastQuickActionsWindow(Solid(1600, 1200, SD.Color.Teal), new SettingsService());
            card.Show();
            PumpFor(80);   // deliberately mid-render: the first preview is still in flight

            Call(card, "TransformImage", SD.RotateFlipType.Rotate90FlipNone);
            card.Dispose();
            PumpFor(1200); // let every posted continuation land on a dead window

            Assert.True(card.IsDisposed);
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // J. All three of the sweep's new WinForms surfaces churned together,
    //    each killed mid-entrance. Nothing may keep timeBeginPeriod(1).
    // ------------------------------------------------------------------

    [Fact]
    public void J_ChurningEveryNewSurfaceMidEntranceLeavesNoClockHolder()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                SD.Rectangle area = WF.Screen.PrimaryScreen!.WorkingArea;
                var anchor = new SD.Point(area.Right - 60, area.Bottom);
                var actions = new List<FirstRunWelcomeWindow.WelcomeAction>
                {
                    new("A", "Capture a region", "Ctrl+Shift+1", () => { }),
                };

                for (int i = 0; i < 12; i++)
                {
                    var welcome = new FirstRunWelcomeWindow(anchor, actions, () => { });
                    var toast = new ToastWindow("Saved", $"shot-{i}.png", null, null, ToastSeverity.Success);
                    var card = new FastQuickActionsWindow(Solid(320, 240, SD.Color.Coral), new SettingsService());
                    welcome.Show();
                    toast.Show();
                    card.Show();
                    PumpFor(20);   // well inside every entrance tween
                    welcome.Dispose();
                    toast.Dispose();
                    card.Dispose();
                }

                PumpFor(1500);
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                PumpFor(100);
            }
        });
    }

    // ------------------------------------------------------------------
    // K. A stitched scrolling capture and a 4K shot through the standing
    //    "frame every shot" backdrop: the padding clamp is what stops a
    //    12,000 px tall capture picking up 660 px of gradient per side.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3840, 2160)]
    [InlineData(1200, 12000)]
    [InlineData(12000, 90)]
    public void K_TheBackdropPaddingStaysSaneAtEverySize(int w, int h)
    {
        int pad = ScreenshotBackdrop.Padding(new SD.Size(w, h));
        Assert.InRange(pad, 24, 96);

        SD.Size canvas = ScreenshotBackdrop.CanvasFor(new SD.Size(w, h));
        Assert.Equal(w + pad * 2, canvas.Width);
        Assert.Equal(h + pad * 2, canvas.Height);

        using var shot = Solid(Math.Min(w, 400), Math.Min(h, 400), SD.Color.White);
        using SD.Bitmap? framed = ScreenshotBackdrop.Compose(shot, ScreenshotBackdrop.Gradient, null);
        Assert.NotNull(framed);
        Assert.Equal(ScreenshotBackdrop.CanvasFor(shot.Size), framed!.Size);

        // None must leave the capture exactly as grabbed.
        Assert.Null(ScreenshotBackdrop.Compose(shot, ScreenshotBackdrop.None, null));
    }

    // ------------------------------------------------------------------
    // L. Keyboard nudge of a framed selection on a monitor ABOVE the
    //    laptop, i.e. negative Y — Isaiah's actual desk.
    // ------------------------------------------------------------------

    [Fact]
    public void L_NudgingOnANegativeYMonitorNeverEscapesTheDesktop()
    {
        // Two monitors above a laptop: the virtual desktop starts well above zero.
        var desktop = new SD.Rectangle(-1920, -1080, 5760, 2160);
        var rect = new SD.Rectangle(-1900, -1070, 40, 30);

        foreach (WF.Keys key in new[] { WF.Keys.Left, WF.Keys.Up, WF.Keys.Right, WF.Keys.Down })
        {
            foreach (bool shift in new[] { false, true })
            {
                foreach (bool ctrl in new[] { false, true })
                {
                    SD.Rectangle current = rect;
                    for (int i = 0; i < 500; i++)
                    {
                        SD.Rectangle? next = WinShot.Capture.SelectionNudge.Apply(
                            current, key, shift, ctrl, desktop);
                        Assert.NotNull(next);
                        current = next!.Value;
                        Assert.True(current.Width >= 1 && current.Height >= 1,
                            $"{key} shift={shift} ctrl={ctrl} collapsed to {current}");
                        Assert.True(desktop.Contains(current),
                            $"{key} shift={shift} ctrl={ctrl} walked to {current}, outside {desktop}");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // M. Automatic image format must never pick JPEG for a capture with
    //    real transparency — JPEG has no alpha and the corners go black.
    // ------------------------------------------------------------------

    [Fact]
    public void M_AutoFormatNeverPicksJpegForATransparentCapture()
    {
        // A trimmed window capture: photographic content, rounded transparent corners.
        using var shot = new SD.Bitmap(600, 400, SD.Imaging.PixelFormat.Format32bppArgb);
        var rng = new Random(7);
        for (int y = 0; y < shot.Height; y++)
            for (int x = 0; x < shot.Width; x++)
                shot.SetPixel(x, y, SD.Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)));
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 12 - y; x++)
                shot.SetPixel(x, y, SD.Color.FromArgb(0, 0, 0, 0));

        Assert.Equal("png", ImageFormatChooser.Choose(shot));
        Assert.Equal("png", ImageFormatChooser.Resolve(ImageFormatChooser.Auto, shot));

        using var tiny = new SD.Bitmap(0 + 1, 1, SD.Imaging.PixelFormat.Format32bppArgb);
        Assert.Equal("png", ImageFormatChooser.Choose(tiny));
    }
}
