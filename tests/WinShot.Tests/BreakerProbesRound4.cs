using System.Diagnostics;
using System.Reflection;
using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Pin;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The fourth review round's probes, kept as tests. Each one failed on the branch before the
/// fix beside it: the scrolling instruction pill stranded at full alpha by a drag inside its
/// own entrance ramp (and permanently, with animations off), a pin's toolbar laid out at its
/// full size inside a client too small to hold it, and a zoomed scrolling capture asking for
/// a full-canvas resample of a window that is mostly off screen.
/// </summary>
public class BreakerProbesRound4
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static object? Priv(object o, string name)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static void SetPriv(object o, string name, object? value)
        => o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static object? Call(object o, string name, params object?[] args)
        => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(o, args);

    // ------------------------------------------------------------------
    // P1 - the scrolling instruction pill, when the drag starts inside its
    //      own 140 ms entrance ramp.
    // ------------------------------------------------------------------

    [Fact]
    public void InstructionPill_DragInsideTheEntranceRamp_StillFadesOut()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            var dlg = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            try
            {
                SetPriv(dlg, "_options",
                    SelectorOptions.ForRegion(null) with { Instruction = "Drag over the part that scrolls" });

                Call(dlg, "FadeInstructionIn");
                PumpFor(40);                     // still mid-entrance (QuickMs = 140)
                Call(dlg, "FadeInstructionOut"); // the user starts dragging
                PumpFor(600);

                double alpha = (double)Priv(dlg, "_instructionAlpha")!;
                Assert.True(alpha <= 0.01d,
                    $"the pill never faded out; alpha settled at {alpha:0.###}");
            }
            finally
            {
                dlg.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(50);
            }
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>
    /// The same pill with animations off system-wide, which is the rubric's own item. Motion
    /// invokes the completion inline, before Run can return, so the handle the exit ramp used
    /// to guard on was never null again and the pill rode the cursor for the whole drag.
    /// </summary>
    [Fact]
    public void InstructionPill_WithAnimationsOff_IsGoneTheMomentTheDragBegins()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            var dlg = new FastRegionSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            try
            {
                SetPriv(dlg, "_options",
                    SelectorOptions.ForRegion(null) with { Instruction = "Drag over the part that scrolls" });

                Call(dlg, "FadeInstructionIn");
                Assert.Equal(1.0d, (double)Priv(dlg, "_instructionAlpha")!, 3);

                Call(dlg, "FadeInstructionOut");
                Assert.Equal(0d, (double)Priv(dlg, "_instructionAlpha")!, 3);
            }
            finally
            {
                dlg.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(50);
            }
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // P2 - a tall pin zoomed in. The paint used to resample and CACHE the
    //      whole pin at client size, and a pin can be much larger than the
    //      screen.
    // ------------------------------------------------------------------

    [Fact]
    public void ZoomingATallPin_ResamplesOnlyTheBandBeingPainted()
    {
        RunSta(() =>
        {
            // A 1,200 x 6,000 scrolling capture, which is an ordinary result of the flow.
            var capture = new SD.Bitmap(1200, 6000, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(capture)) g.Clear(SD.Color.SteelBlue);

            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                // Ctrl-wheel past 1:1, which is what anyone does to a pin they want to read.
                SetPriv(pin, "_scale", 1.5d);
                Call(pin, "ApplyScale");
                Assert.Equal(new SD.Size(1800, 9000), pin.ClientSize);

                var client = new SD.Rectangle(SD.Point.Empty, pin.ClientSize);
                // What Windows hands a paint: the update region, clipped to what is visible.
                SD.Rectangle band = Band(client, new SD.Rectangle(0, 0, 1800, 1000));
                Assert.True(band.Height < client.Height, $"the band {band} is the whole client");
                Assert.True(band.Contains(new SD.Rectangle(0, 0, 1800, 1000)),
                    $"the band {band} does not cover the area being painted");

                var bmp = (SD.Bitmap)Call(pin, "ScaledImage", pin.ClientSize, band)!;
                Assert.Equal(band.Size, bmp.Size);
            }
            finally
            {
                pin.Dispose();
                PumpFor(50);
            }
        });
    }

    /// <summary>The band a 32,000 px scrolling capture zoomed to MaxScale asks to resample.
    /// Cheap: no bitmap is built, only the geometry.</summary>
    [Fact]
    public void ZoomingAnEnormousPin_DoesNotAskForAnUnallocatableCanvas()
    {
        RunSta(() =>
        {
            var capture = new SD.Bitmap(1000, 32_000, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                SetPriv(pin, "_scale", PinInteraction.MaxScale);
                Call(pin, "ApplyScale");
                var client = new SD.Rectangle(SD.Point.Empty, pin.ClientSize);
                Assert.True((long)client.Width * client.Height > 200_000_000L,
                    "this pin is supposed to be enormous");

                // Even asked for the whole thing, the band stays inside a screen's worth.
                SD.Rectangle band = Band(client, client);
                long bytes = (long)band.Width * band.Height * 4;
                Assert.True(bytes < 64L * 1024 * 1024,
                    $"one paint would allocate a {band.Size} 32bpp copy ({bytes / 1024 / 1024} MB) " +
                    "of a pin whose visible part is one screen");
            }
            finally
            {
                pin.Dispose();
                PumpFor(30);
            }
        });
    }

    private static SD.Rectangle Band(SD.Rectangle client, SD.Rectangle clip)
        => (SD.Rectangle)typeof(FastPinWindow)
            .GetMethod("ResampleBand", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { client, clip })!;

    // ------------------------------------------------------------------
    // P3 - a pin locked while it is narrower than even the two-chip bar.
    // ------------------------------------------------------------------

    [Fact]
    public void AVeryNarrowLockedPin_KeepsAtLeastOneReachableChip()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            var capture = new SD.Bitmap(24, 24, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                SetPriv(pin, "_mouseInside", true);
                Call(pin, "SetLocked", true);
                AssertEveryChipIsReachable(pin);
            }
            finally
            {
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(50);
            }
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // P8 - a short pin. The bar was width-trimmed but never height-checked.
    // ------------------------------------------------------------------

    [Fact]
    public void AShortPin_StillDrawsItsToolbarInsideItsOwnClient()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            // A pinned single-line strip: 400 x 20 is an ordinary crop of a status bar.
            var capture = new SD.Bitmap(400, 20, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                SetPriv(pin, "_mouseInside", true);
                var bar = (SD.Rectangle)Call(pin, "ToolbarBounds")!;
                Assert.True(bar.Bottom <= pin.ClientSize.Height,
                    $"the toolbar {bar} is drawn below the {pin.ClientSize} client, so every " +
                    "chip is clipped away");
                AssertEveryChipIsReachable(pin);

                // And locked, where an unreachable chip is not a degradation but a trap:
                // every point that is not a chip answers HTTRANSPARENT.
                Call(pin, "SetLocked", true);
                AssertEveryChipIsReachable(pin);
            }
            finally
            {
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(50);
            }
        });
    }

    /// <summary>An ordinary pin still gets the full-size bar: the shrink is for the pins that
    /// cannot hold one, and must not quietly cost every other pin its chip size.</summary>
    [Fact]
    public void AnOrdinaryPin_KeepsTheFullSizeToolbar()
    {
        RunSta(() =>
        {
            var capture = new SD.Bitmap(800, 600, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                SetPriv(pin, "_mouseInside", true);
                var bar = (SD.Rectangle)Call(pin, "ToolbarBounds")!;
                int expected = PinInteraction.ScaleLogical(28, pin.DeviceDpi) +
                    (PinInteraction.ScaleLogical(6, pin.DeviceDpi) * 2);
                Assert.Equal(expected, bar.Height);
                Assert.Equal(PinInteraction.ScaleLogical(4, pin.DeviceDpi), bar.Top);
                AssertEveryChipIsReachable(pin);
            }
            finally
            {
                pin.Dispose();
                PumpFor(30);
            }
        });
    }

    private static void AssertEveryChipIsReachable(FastPinWindow pin)
    {
        var visible = (int[])pin.GetType()
            .GetProperty("VisibleButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pin)!;
        Assert.NotEmpty(visible);

        // Whatever survives the trim has to be inside the client, or a locked pin has no way
        // out that a click can reach.
        foreach (int i in visible)
        {
            var bounds = (SD.Rectangle)Call(pin, "ButtonBounds", i)!;
            Assert.True(
                bounds.Right <= pin.ClientSize.Width && bounds.Left >= 0 &&
                bounds.Bottom <= pin.ClientSize.Height && bounds.Top >= 0,
                $"chip {i} at {bounds} is outside the {pin.ClientSize} client");
            Assert.Equal(i, (int)Call(pin, "HitTestButton",
                new SD.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2))!);
        }
    }

    // ------------------------------------------------------------------
    // P4 - disposing a pin in the middle of a lock swap.
    // ------------------------------------------------------------------

    [Fact]
    public void PinDisposedMidLockSwap_ReleasesItsHolder()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            var capture = new SD.Bitmap(400, 300, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                SetPriv(pin, "_mouseInside", true);
                SetPriv(pin, "_chromeAlpha", 1.0d);
                Call(pin, "SetLocked", true); // starts the SwapMs glide
                PumpFor(40);
                Assert.NotNull(Priv(pin, "_lockFade"));
            }
            finally
            {
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(80);
            }
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // P5 - the whole app "starts with animations off".
    // ------------------------------------------------------------------

    [Fact]
    public void WithAnimationsOff_TheNewSurfacesSettleInlineAndHoldNothing()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;
            var capture = new SD.Bitmap(300, 200, SD.Imaging.PixelFormat.Format32bppPArgb);
            var pin = new FastPinWindow(capture, new SettingsService())
            {
                StartPosition = WF.FormStartPosition.Manual,
                Location = new SD.Point(-20_000, -20_000),
            };
            try
            {
                pin.Show();
                PumpFor(30);
                SetPriv(pin, "_mouseInside", true);
                Call(pin, "UpdateChromeAlpha");
                Assert.Equal(1.0d, (double)Priv(pin, "_chromeAlpha")!, 3);

                Call(pin, "SetLocked", true);
                Assert.Null(Priv(pin, "_lockSwapFrom"));
                Assert.Equal(1.0d, (double)Priv(pin, "_lockSwap")!, 3);
                Call(pin, "UpdateImageAlpha");
                Assert.True((double)Priv(pin, "_imageAlpha")! < 1.0d);

                FastPinWindow.ToggleAllVisibility();
                Assert.False(pin.Visible);
                FastPinWindow.ToggleAllVisibility();
                Assert.True(pin.Visible);
                Assert.Equal(1.0d, pin.Opacity, 3);
            }
            finally
            {
                pin.Dispose();
                Motion.AnimationsEnabled = previous;
                PumpFor(50);
            }
            Assert.Equal(0, Motion.Holders);
        });
    }

    // ------------------------------------------------------------------
    // P6 - the tall-capture flyout on a monitor above the laptop (negative Y)
    //      at 150 %, which is Isaiah's actual desk.
    // ------------------------------------------------------------------

    [Fact]
    public void TallPreview_OnANegativeYMonitorAt150Percent_StaysAboveTheCard()
    {
        var working = new SD.Rectangle(-1920, -1080, 1920, 1040);
        var card = new SD.Rectangle(-360, -180, 300, 150);
        SD.Rectangle place = TallPreviewPlace(new SD.Size(900, 6000), working, card, 144);

        Assert.True(place.Bottom <= card.Top, $"{place} overlaps the card at {card}");
        Assert.True(working.Contains(place), $"{place} escaped the working area {working}");
    }

    private static SD.Rectangle TallPreviewPlace(SD.Size image, SD.Rectangle working, SD.Rectangle card, int dpi)
    {
        Type layout = typeof(FastPinWindow).Assembly
            .GetType("WinShot.Overlay.TallCapturePreviewLayout")!;
        return (SD.Rectangle)layout.GetMethod("Place", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { image, working, card, dpi })!;
    }

    // ------------------------------------------------------------------
    // P7 - saved colours from a five-thousand entry stored list.
    // ------------------------------------------------------------------

    [Fact]
    public void SavedColors_HugeStoredList_IsCappedCheaply()
    {
        var huge = new List<string>();
        for (int i = 0; i < 5000; i++) huge.Add($"#{i % 256:X2}00FF");

        var sw = Stopwatch.StartNew();
        List<string> sane = SavedColors.Sanitize(huge);
        sw.Stop();

        Assert.True(sane.Count <= SavedColors.MaxCount);
        Assert.True(sw.ElapsedMilliseconds < 50, $"sanitising took {sw.ElapsedMilliseconds} ms");
    }
}
