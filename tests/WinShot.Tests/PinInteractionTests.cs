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
