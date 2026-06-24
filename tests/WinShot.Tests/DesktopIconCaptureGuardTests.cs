using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class DesktopIconCaptureGuardTests
{
    [Fact]
    public void Run_DoesNotTouchIconsWhenSettingIsOff()
    {
        var calls = new List<string>();

        int result = DesktopIconCaptureGuard.Run(
            hideDuringCapture: false,
            iconsVisible: () => throw new InvalidOperationException("should not query"),
            hideIcons: () => calls.Add("hide"),
            showIcons: () => calls.Add("show"),
            wait: _ => calls.Add("wait"),
            capture: () => 42);

        Assert.Equal(42, result);
        Assert.Empty(calls);
    }

    [Fact]
    public void Run_DoesNotRestoreWhenIconsWereAlreadyHidden()
    {
        var calls = new List<string>();

        DesktopIconCaptureGuard.Run(
            hideDuringCapture: true,
            iconsVisible: () => false,
            hideIcons: () => calls.Add("hide"),
            showIcons: () => calls.Add("show"),
            wait: _ => calls.Add("wait"),
            capture: () => 0);

        Assert.Empty(calls);
    }

    [Fact]
    public void Run_HidesWaitsCapturesAndRestoresWhenEnabled()
    {
        var calls = new List<string>();

        DesktopIconCaptureGuard.Run(
            hideDuringCapture: true,
            iconsVisible: () => true,
            hideIcons: () => calls.Add("hide"),
            showIcons: () => calls.Add("show"),
            wait: ms => calls.Add($"wait:{ms}"),
            capture: () =>
            {
                calls.Add("capture");
                return 0;
            });

        Assert.Equal(new[] { "hide", "wait:120", "capture", "show" }, calls);
    }

    [Fact]
    public void Run_RestoresIconsWhenCaptureThrows()
    {
        var calls = new List<string>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconCaptureGuard.Run<int>(
                hideDuringCapture: true,
                iconsVisible: () => true,
                hideIcons: () => calls.Add("hide"),
                showIcons: () => calls.Add("show"),
                wait: _ => calls.Add("wait"),
                capture: () => throw new InvalidOperationException("capture failed")));

        Assert.Equal("capture failed", ex.Message);
        Assert.Equal(new[] { "hide", "wait", "show" }, calls);
    }
}
