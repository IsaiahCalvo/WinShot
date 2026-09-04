using System.Reflection;
using WinShot.Core;
using WinShot.Pin;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class PinInteractionTests
{
    [Theory]
    [InlineData(1.0, 120, 1.1)]
    [InlineData(1.0, -120, 0.9090909090909091)]
    [InlineData(3.0, 120, 3.0)]
    [InlineData(0.2, -120, 0.2)]
    // A huge image's fit scale sits below MinScale: zooming in steps smoothly from it
    // (regression: the old MinScale clamp snapped 0.078 -> 0.2, tripling the pin).
    [InlineData(0.078, 120, 0.0858)]
    [InlineData(0.078, -120, 0.078)]
    public void AdjustScale_UsesSharedWheelBounds(double current, int delta, double expected)
    {
        Assert.Equal(expected, PinInteraction.AdjustScale(current, delta), precision: 10);
    }

    [Theory]
    [InlineData(0.9, 120, 1.0)]
    [InlineData(0.9, -120, 0.8)]
    [InlineData(1.0, 120, 1.0)]
    [InlineData(0.3, -120, 0.3)]
    public void AdjustOpacity_UsesSharedWheelBounds(double current, int delta, double expected)
    {
        Assert.Equal(expected, PinInteraction.AdjustOpacity(current, delta), precision: 10);
    }

    [Theory]
    [InlineData(1.0, 0.85)]
    [InlineData(0.2, 0.17)]
    [InlineData(0.05, 0.1)]
    public void LockedOpacity_DimsWithoutBecomingInvisible(double current, double expected)
    {
        Assert.Equal(expected, PinInteraction.LockedOpacity(current), precision: 10);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 10)]
    public void NudgeStep_UsesShiftForLargerSteps(bool shift, int expected)
    {
        Assert.Equal(expected, PinInteraction.NudgeStep(shift));
    }

    [Theory]
    [InlineData(26, 96, 26)]
    [InlineData(26, 144, 39)]
    [InlineData(26, 192, 52)]
    [InlineData(6, 72, 6)]
    public void ScaleLogical_UsesEffectiveDpi(int logical, int dpi, int expected)
    {
        Assert.Equal(expected, PinInteraction.ScaleLogical(logical, dpi));
    }

    [Fact]
    public void CascadeLocation_StaysInsideNegativeCoordinateMonitor()
    {
        var monitor = new SD.Rectangle(-1920, 0, 1920, 1040);

        SD.Point result = PinInteraction.CascadeLocation(
            monitor,
            new SD.Size(1800, 900),
            cascadeIndex: 7,
            logicalOffset: 24,
            dpi: 96);

        Assert.Equal(new SD.Point(-1800, 140), result);
    }

    [Fact]
    public void ClampLocation_AnchorsOversizedPinAtMonitorOrigin()
    {
        var monitor = new SD.Rectangle(1920, -1080, 1280, 1024);

        SD.Point result = PinInteraction.ClampLocation(
            monitor,
            new SD.Size(1600, 1200),
            new SD.Point(2500, -500));

        Assert.Equal(monitor.Location, result);
    }

    [Theory]
    [InlineData(@"D:\Captures", @"C:\Pictures\WinShot", @"D:\Captures")]
    [InlineData(@"  D:\Captures  ", @"C:\Pictures\WinShot", @"D:\Captures")]
    [InlineData("", @"C:\Pictures\WinShot", @"C:\Pictures\WinShot")]
    [InlineData("   ", @"C:\Pictures\WinShot", @"C:\Pictures\WinShot")]
    public void ResolveSaveFolder_PrefersConfiguredFolder(string configured, string fallback, string expected)
    {
        Assert.Equal(expected, PinInteraction.ResolveSaveFolder(configured, fallback));
    }

    /// <summary>
    /// The pin is exactly the scaled image. It used to be two pixels larger with the image
    /// inset by one over a black BackColor, which baked a dark hairline around every pin --
    /// clearly visible on a light screenshot, and there with PinnedBorder off.
    /// </summary>
    [Fact]
    public void ThePinIsExactlyTheScaledImage_WithNoBakedFrame()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap, new SettingsService());
            pin.CreateControl();

            SetScale(pin, 1.0);
            Assert.Equal(new SD.Size(320, 180), pin.ClientSize);

            SetScale(pin, 0.5);
            Assert.Equal(new SD.Size(160, 90), pin.ClientSize);

            // And the scale derived back from a resized client area agrees, so a border
            // drag and a wheel notch cannot disagree about how big the pin is.
            pin.ClientSize = new SD.Size(640, 360);
            typeof(FastPinWindow)
                .GetMethod("SyncScaleFromClientSize", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, null);
            Assert.Equal(2.0, (double)typeof(FastPinWindow)
                .GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pin)!, 3);
        });
    }

    /// <summary>
    /// A settings-less pin has to look like a pin. The fallbacks were false, so any caller
    /// that did not hand over a SettingsService got a hard-square, shadowless window.
    /// </summary>
    [Fact]
    public void APinWithNoSettings_StillGetsRoundedCornersAndAShadow()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap);
            pin.CreateControl();

            Assert.NotNull(pin.Region);
            var createParams = (WF.CreateParams)typeof(FastPinWindow)
                .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pin)!;
            Assert.NotEqual(0, createParams.ClassStyle & 0x00020000);
        });
    }

    /// <summary>
    /// The hover chrome ramps instead of appearing at full alpha on the next paint. It is the
    /// only state change this window has, so it is the whole of the pin's hover language.
    /// Asserts the ramp finishes in both directions and releases the timer-resolution hold.
    /// </summary>
    [Fact]
    public void HoverChromeFadesInAndOut_AndLeavesNoClockHolder()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                Assert.Equal(0, Motion.Holders);
                using var bitmap = new SD.Bitmap(320, 180);
                using var pin = new FastPinWindow(bitmap, new SettingsService());
                pin.CreateControl();

                Assert.Equal(0d, ChromeAlpha(pin));

                SetField(pin, "_mouseInside", true);
                UpdateChrome(pin);
                Assert.True(Motion.Holders > 0, "the fade should be running");
                PumpUntil(() => Motion.Holders == 0);
                Assert.Equal(1d, ChromeAlpha(pin), 3);

                SetField(pin, "_mouseInside", false);
                UpdateChrome(pin);
                PumpUntil(() => Motion.Holders == 0);
                Assert.Equal(0d, ChromeAlpha(pin), 3);
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
            }
        });
    }

    /// <summary>Closing a pin mid-fade must not strand the process-wide clock.</summary>
    [Fact]
    public void ClosingAPinMidFade_ReleasesTheClockHolder()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                Assert.Equal(0, Motion.Holders);
                using var bitmap = new SD.Bitmap(320, 180);
                var pin = new FastPinWindow(bitmap, new SettingsService());
                pin.CreateControl();
                SetField(pin, "_mouseInside", true);
                UpdateChrome(pin);

                pin.Close();
                pin.Dispose();
                PumpUntil(() => Motion.Holders == 0);
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
            }
        });
    }

    /// <summary>
    /// The zoom chip's label. It replaced an 800 ms cursor pill, so it has to be readable at
    /// every scale a pin can reach -- including the fit scale of a 32,000 px scrolling
    /// capture, which rounds to 0 % if you just print the integer.
    /// </summary>
    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.5, "50%")]
    [InlineData(3.0, "300%")]
    [InlineData(1.324, "132%")]
    [InlineData(0.078, "7.8%")]
    [InlineData(0.004, "0.4%")]
    [InlineData(0.0001, "0.1%")]
    public void TheZoomChipReadsTheScale(double scale, string expected)
        => Assert.Equal(expected, FastPinWindow.ZoomChipText(scale));

    /// <summary>
    /// A locked pin keeps its own way out. Locking used to put WS_EX_TRANSPARENT on the whole
    /// window, so the pin received no mouse messages at all and the tray's "Unlock pinned
    /// windows" was the only escape. The two chips it still offers have to be real rects that
    /// hit-test, and the other two actions have to be gone -- they would need exactly the
    /// click that lock exists to pass through.
    /// </summary>
    [Fact]
    public void ALockedPinKeepsItsLockAndCloseChipsHitTestable()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(600, 400);
            using var pin = new FastPinWindow(bitmap, new SettingsService());
            pin.CreateControl();
            SetField(pin, "_mouseInside", true);

            Assert.Equal(new[] { 0, 1, 2, 3 }, VisibleButtons(pin));
            SetLocked(pin, true);
            Assert.Equal(new[] { 2, 3 }, VisibleButtons(pin));

            // Lock and Close are hit-testable; Copy and Save are not offered at all.
            SD.Rectangle lockChip = ButtonBounds(pin, 2);
            SD.Rectangle closeChip = ButtonBounds(pin, 3);
            Assert.False(lockChip.IsEmpty);
            Assert.False(closeChip.IsEmpty);
            Assert.True(ButtonBounds(pin, 0).IsEmpty);
            Assert.True(ButtonBounds(pin, 1).IsEmpty);
            Assert.False(lockChip.IntersectsWith(closeChip));

            Assert.Equal(2, HitTest(pin, Centre(lockChip)));
            Assert.Equal(3, HitTest(pin, Centre(closeChip)));
            Assert.Equal(-1, HitTest(pin, new SD.Point(pin.ClientSize.Width / 2, pin.ClientSize.Height - 4)));

            // Unlocking puts everything back.
            SetLocked(pin, false);
            Assert.Equal(new[] { 0, 1, 2, 3 }, VisibleButtons(pin));
        });
    }

    /// <summary>
    /// The dim a locked pin takes is hover-scoped and applies to the image, not the window:
    /// window opacity would take the chips painted over it down too, and a permanent dim was
    /// the old behaviour's other half. Window opacity itself must not move.
    /// </summary>
    [Fact]
    public void ALockedPinDimsOnlyWhileHovered_AndOnlyItsImage()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => false;   // jump straight to the end state
            try
            {
                using var bitmap = new SD.Bitmap(600, 400);
                using var pin = new FastPinWindow(bitmap, new SettingsService());
                pin.CreateControl();

                SetLocked(pin, true);
                Assert.Equal(1d, ImageAlpha(pin), 3);
                Assert.Equal(1d, pin.Opacity, 3);

                SetField(pin, "_mouseInside", true);
                UpdateImage(pin);
                Assert.Equal(PinInteraction.LockedOpacity(1d), ImageAlpha(pin), 3);
                Assert.Equal(1d, pin.Opacity, 3);

                SetField(pin, "_mouseInside", false);
                UpdateImage(pin);
                Assert.Equal(1d, ImageAlpha(pin), 3);

                // And an unlocked pin never dims, hovered or not.
                SetLocked(pin, false);
                SetField(pin, "_mouseInside", true);
                UpdateImage(pin);
                Assert.Equal(1d, ImageAlpha(pin), 3);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
            }
        });
    }

    /// <summary>
    /// Toggle-all is one gesture over the whole set, not a per-pin toggle: with two pins and
    /// one already hidden, toggling each on its own leaves them alternating forever. Any
    /// visible pin means "hide"; nothing visible means "bring them back".
    /// </summary>
    [Fact]
    public void TogglingPinVisibility_MovesEveryPinTheSameWay()
    {
        RunSta(() =>
        {
            FastPinWindow.CloseAll();
            Assert.False(FastPinWindow.AnyOpen);

            using var first = new FastPinWindow(new SD.Bitmap(200, 120), new SettingsService());
            using var second = new FastPinWindow(new SD.Bitmap(200, 120), new SettingsService());
            first.StartPosition = WF.FormStartPosition.Manual;
            second.StartPosition = WF.FormStartPosition.Manual;
            first.Location = new SD.Point(-20_000, -20_000);
            second.Location = new SD.Point(-20_000, -20_000);
            first.Show();
            second.Show();
            Assert.True(FastPinWindow.AnyOpen);

            // One already hidden: the set still moves together.
            second.Hide();
            FastPinWindow.ToggleAllVisibility();
            Assert.False(first.Visible);
            Assert.False(second.Visible);

            FastPinWindow.ToggleAllVisibility();
            Assert.True(first.Visible);
            Assert.True(second.Visible);

            FastPinWindow.CloseAll();
            Assert.False(FastPinWindow.AnyOpen);
        });
    }

    /// <summary>Both statics are safe with no pins on screen -- they are bound to global
    /// hotkeys, so they are reachable at any moment.</summary>
    [Fact]
    public void ThePinCommandsAreSafeWithNothingPinned()
    {
        RunSta(() =>
        {
            FastPinWindow.CloseAll();
            FastPinWindow.ToggleAllVisibility();
            FastPinWindow.CloseAll();
            FastPinWindow.UnlockAllPins();
            Assert.False(FastPinWindow.AnyOpen);
        });
    }

    private static void SetLocked(FastPinWindow pin, bool locked)
        => typeof(FastPinWindow)
            .GetMethod("SetLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { locked });

    private static SD.Point Centre(SD.Rectangle rect)
        => new(rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));

    private static int[] VisibleButtons(FastPinWindow pin)
        => (int[])typeof(FastPinWindow)
            .GetProperty("VisibleButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pin)!;

    private static SD.Rectangle ButtonBounds(FastPinWindow pin, int index)
        => (SD.Rectangle)typeof(FastPinWindow)
            .GetMethod("ButtonBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { index })!;

    private static int HitTest(FastPinWindow pin, SD.Point point)
        => (int)typeof(FastPinWindow)
            .GetMethod("HitTestButton", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, new object[] { point })!;

    private static double ImageAlpha(FastPinWindow pin)
        => (double)typeof(FastPinWindow)
            .GetField("_imageAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pin)!;

    private static void UpdateImage(FastPinWindow pin)
        => typeof(FastPinWindow)
            .GetMethod("UpdateImageAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, null);

    private static double ChromeAlpha(FastPinWindow pin)
        => (double)typeof(FastPinWindow)
            .GetField("_chromeAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(pin)!;

    private static void UpdateChrome(FastPinWindow pin)
        => typeof(FastPinWindow)
            .GetMethod("UpdateChromeAlpha", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, null);

    private static void SetField(FastPinWindow pin, string name, object value)
        => typeof(FastPinWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pin, value);

    private static void PumpUntil(Func<bool> condition, int ms = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < ms)
        {
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static void SetScale(FastPinWindow pin, double scale)
    {
        typeof(FastPinWindow)
            .GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pin, scale);
        typeof(FastPinWindow)
            .GetMethod("ApplyScale", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, null);
    }

    [Fact]
    public void PinVisualSettingsAndAccessibility_AreApplied()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.PinnedRoundedCorners = true;
            settings.Current.PinnedShadow = true;
            settings.Current.PinnedBorder = true;

            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap, settings);
            pin.CreateControl();

            Assert.NotNull(pin.Region);

            var createParams = (WF.CreateParams)typeof(FastPinWindow)
                .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pin)!;
            Assert.NotEqual(0, createParams.ClassStyle & 0x00020000);

            WF.AccessibleObject accessible = pin.AccessibilityObject;
            Assert.Equal("Pinned screenshot", accessible.Name);
            Assert.Equal(4, accessible.GetChildCount());
            Assert.Equal(new[] { "Copy", "Save", "Lock", "Close" },
                Enumerable.Range(0, 4).Select(i => accessible.GetChild(i)!.Name));
            Assert.All(Enumerable.Range(0, 4), i =>
            {
                WF.AccessibleObject child = accessible.GetChild(i)!;
                Assert.Equal(WF.AccessibleRole.PushButton, child.Role);
                Assert.False(string.IsNullOrWhiteSpace(child.Help));
                Assert.Equal("Press", child.DefaultAction);
            });

            accessible.GetChild(1)!.Select(WF.AccessibleSelection.TakeFocus);
            Assert.Equal("Save", accessible.GetFocused()!.Name);

            Assert.True(InvokeCommandKey(pin, WF.Keys.Tab));
            Assert.Equal("Lock", accessible.GetFocused()!.Name);
            Assert.True(InvokeCommandKey(pin, WF.Keys.Shift | WF.Keys.Tab));
            Assert.Equal("Save", accessible.GetFocused()!.Name);
        });
    }

    [Fact]
    public void DisabledPinVisualSettings_KeepSquareWindowWithoutLegacyShadow()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.PinnedRoundedCorners = false;
            settings.Current.PinnedShadow = false;
            settings.Current.PinnedBorder = false;

            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap, settings);
            pin.CreateControl();

            Assert.Null(pin.Region);
            var createParams = (WF.CreateParams)typeof(FastPinWindow)
                .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pin)!;
            Assert.Equal(0, createParams.ClassStyle & 0x00020000);
        });
    }

    [Fact]
    public void RoundedPinRegion_TracksOffscreenResizes()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.PinnedRoundedCorners = true;

            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap, settings);
            pin.CreateControl();

            foreach (SD.Size size in new[]
                     {
                         new SD.Size(320, 180),
                         new SD.Size(701, 141),
                         new SD.Size(141, 701),
                         new SD.Size(47, 31),
                     })
            {
                pin.ClientSize = size;
                Assert.NotNull(pin.Region);
                Assert.False(pin.Region!.IsVisible(0, 0));
                Assert.False(pin.Region.IsVisible(size.Width - 1, 0));
                Assert.False(pin.Region.IsVisible(0, size.Height - 1));
                Assert.False(pin.Region.IsVisible(size.Width - 1, size.Height - 1));
                Assert.True(pin.Region.IsVisible(size.Width / 2, size.Height / 2));
            }
        });
    }

    [Fact]
    public void MiddleClick_ClosesPin()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(320, 180);
            using var pin = new FastPinWindow(bitmap, new SettingsService());
            pin.CreateControl();

            typeof(FastPinWindow)
                .GetMethod(
                    "OnMouseDown",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(object), typeof(WF.MouseEventArgs) },
                    modifiers: null)!
                .Invoke(pin, new object?[]
                {
                    null,
                    new WF.MouseEventArgs(WF.MouseButtons.Middle, 1, 20, 20, 0),
                });

            Assert.True(pin.IsDisposed);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static bool InvokeCommandKey(FastPinWindow pin, WF.Keys keys)
    {
        var message = WF.Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        object[] args = { message, keys };
        return (bool)typeof(FastPinWindow)
            .GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(pin, args)!;
    }
}
