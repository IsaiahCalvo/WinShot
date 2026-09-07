using System.Reflection;
using System.Threading;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The approved pin swap: outlined among its stroked siblings until this capture is actually
/// pinned, solid after. Shares the Motion refcount collection because the swap runs a tween
/// and the holder count it is checked against is process-global.
/// </summary>
[Collection("MotionRefcount")]
public class QuickAccessPinGlyphTests
{
    private const string Outline = "quick-access-pin.svg";
    private const string Filled = "quick-access-pin-filled.svg";

    [Fact]
    public void PinningSwapsTheOutlineForTheSolidMark_AndReleasesItsTween()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            bool pinned = false;
            overlay.PinRequested += _ => pinned = true;

            SD.Bitmap? before = PinIcon(overlay);
            Assert.NotNull(before);
            SD.Color glyph = GlyphColor(overlay);
            Assert.Same(SvgIcons.Get(Outline, before!.Width, glyph), before);

            InvokeKey(overlay, WF.Keys.P);
            Assert.True(pinned);

            SD.Bitmap? after = PinIcon(overlay);
            Assert.NotNull(after);
            Assert.NotSame(before, after);
            Assert.Same(SvgIcons.Get(Filled, before.Width, glyph), after);

            // The cross-fade must end, and end holding nothing.
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);
            Assert.Null(typeof(FastQuickActionsWindow)
                .GetField("_pinGlyphTween", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay));
        });
    }

    [Fact]
    public void PinningTwiceDoesNotRestartTheSwap()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            int requests = 0;
            overlay.PinRequested += _ => requests++;

            InvokeKey(overlay, WF.Keys.P);
            PumpUntil(() => Motion.Holders == 0);
            SD.Bitmap? afterFirst = PinIcon(overlay);

            InvokeKey(overlay, WF.Keys.P);
            // Pinning again is still a real pin; the glyph is simply already solid.
            Assert.Equal(2, requests);
            Assert.Same(afterFirst, PinIcon(overlay));

            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);
        });
    }

    [Fact]
    public void ClosingTheCardMidSwapReleasesTheTween()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            InvokeKey(overlay, WF.Keys.P);

            // Disposed with the cross-fade still in flight — the case that would otherwise
            // hold a Motion refcount for the life of the process.
            overlay.Dispose();
            PumpUntil(() => Motion.Holders == 0);
            Assert.Equal(0, Motion.Holders);
        });
    }

    private static SD.Bitmap? PinIcon(FastQuickActionsWindow overlay)
    {
        object? button = typeof(FastQuickActionsWindow)
            .GetField("_pinButton", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay);
        return button?.GetType().GetProperty("Icon")!.GetValue(button) as SD.Bitmap;
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

    /// <summary>Motion.Run drives a WinForms timer, which needs a pump to tick.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            WF.Application.DoEvents();
            Thread.Sleep(10);
        }
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
        Assert.Null(failure);
    }
}
