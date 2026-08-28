using WinShot.Capture;
using WinShot.Core;
using Xunit;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

public class SelectorMagnifierToggleTests
{
    private static FastRegionSelectorDialog CreateSelector() =>
        new(() => Task.FromResult(new List<WindowInfo>()), settings: null);

    [Fact]
    public void AltTogglesTheMagnifier()
    {
        using var selector = CreateSelector();
        bool before = selector.MagnifierVisible;

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));
        Assert.Equal(!before, selector.MagnifierVisible);

        selector.HandleKeyUp(new WF.KeyEventArgs(WF.Keys.Menu));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));
        Assert.Equal(before, selector.MagnifierVisible);

        // Escape completes the session, releasing the follow-motion clock the
        // toggle may have started (MotionTests asserts absolute holder counts).
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }

    [Fact]
    public void HeldAltDoesNotRetoggleOnKeyRepeat()
    {
        using var selector = CreateSelector();
        bool before = selector.MagnifierVisible;

        // Key-repeat: multiple KeyDowns with no KeyUp in between flip exactly once.
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));
        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Menu | WF.Keys.Alt));
        Assert.Equal(!before, selector.MagnifierVisible);

        selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
    }
}
