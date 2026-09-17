using System.Reflection;
using System.Threading;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The card's pin: one glyph, and one pin per capture.
///
/// The sweep shipped an outline/filled PAIR here — a stroked pushpin swapped for a solid twin
/// the moment a capture was pinned. It was reverted: the card's own pin is the FILLED angled
/// thumbtack it has always been, so the "outline" half of the pair was a new drawing on a
/// pinned surface, and at 16-32 px the swap between two solid marks says nothing. What is
/// left is the part that was a real fix — the card's exit is animated, so it is still on
/// screen after a pin and a second press must not pin the same shot twice.
/// </summary>
[Collection("MotionRefcount")]
public class QuickAccessPinGlyphTests
{
    private const string PinAsset = "quick-access-pin.svg";

    [Fact]
    public void TheCardDrawsTheShippedPinAndNeverSwapsIt()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());

            SD.Bitmap? before = PinIcon(overlay);
            Assert.NotNull(before);
            Assert.Same(SvgIcons.Get(PinAsset, before!.Width, GlyphColor(overlay)), before);

            SD.Bitmap? whenHandlerRan = null;
            overlay.PinRequested += o => whenHandlerRan = PinIcon(o);
            InvokeKey(overlay, WF.Keys.P);

            Assert.Same(before, whenHandlerRan);
            Assert.Same(before, PinIcon(overlay));
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>
    /// The card's exit is animated, so it is still on screen for a few hundred ms after a
    /// pin. A second press landing inside that window must not pin the same shot twice.
    /// </summary>
    [Fact]
    public void PinningTwiceDoesNotPinTwice()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            int requests = 0;
            overlay.PinRequested += _ => requests++;

            InvokeKey(overlay, WF.Keys.P);
            InvokeKey(overlay, WF.Keys.P);

            Assert.Equal(1, requests);
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>The pin button is the first one the card builds, at the top-left.</summary>
    private static SD.Bitmap? PinIcon(FastQuickActionsWindow overlay)
    {
        var buttons = (System.Collections.IList)typeof(FastQuickActionsWindow)
            .GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay)!;
        object button = buttons[0]!;
        Assert.Equal("Pin", button.GetType().GetProperty("AccessibleName")!.GetValue(button));
        return button.GetType().GetProperty("Icon")!.GetValue(button) as SD.Bitmap;
    }

    private static SD.Color GlyphColor(FastQuickActionsWindow overlay)
    {
        object visuals = typeof(FastQuickActionsWindow)
            .GetField("_visuals", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay)!;
        return (SD.Color)visuals.GetType().GetProperty("Glyph")!.GetValue(visuals)!;
    }

    private static void InvokeKey(FastQuickActionsWindow overlay, WF.Keys keys)
        => typeof(FastQuickActionsWindow)
            .GetMethod("OnOverlayKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, [overlay, new WF.KeyEventArgs(keys)]);

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
        Assert.Null(failure);
    }
}
