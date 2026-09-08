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
/// pinned, solid after. Shares the Motion refcount collection because the assertion that the
/// swap takes NO tween is checked against a process-global holder count.
/// </summary>
[Collection("MotionRefcount")]
public class QuickAccessPinGlyphTests
{
    private const string Outline = "quick-access-pin.svg";
    private const string Filled = "quick-access-pin-filled.svg";

    /// <summary>
    /// A pin dismisses the card — that is the card's own shipped behaviour and it is pinned,
    /// so the swap has to be done by the time the handler runs the pin and the exit starts.
    /// Instant, and holding no high-resolution clock: a 140 ms cross-fade here would play on
    /// a surface already flying off screen.
    /// </summary>
    [Fact]
    public void PinningSwapsTheOutlineForTheSolidMark_BeforeTheHandlerRuns()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());

            SD.Bitmap? before = PinIcon(overlay);
            Assert.NotNull(before);
            SD.Color glyph = GlyphColor(overlay);
            Assert.Same(SvgIcons.Get(Outline, before!.Width, glyph), before);

            SD.Bitmap? whenHandlerRan = null;
            overlay.PinRequested += o => whenHandlerRan = PinIcon(o);

            InvokeKey(overlay, WF.Keys.P);

            // The App handler closes the card in this same call, so "solid by then" is the
            // only thing that can ever be seen.
            Assert.Same(SvgIcons.Get(Filled, before.Width, glyph), whenHandlerRan);
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
            SD.Bitmap? afterFirst = PinIcon(overlay);
            InvokeKey(overlay, WF.Keys.P);

            Assert.Equal(1, requests);
            Assert.Same(afterFirst, PinIcon(overlay));
            Assert.Equal(0, Motion.Holders);
        });
    }

    [Fact]
    public void APinnedCardRebuiltByAResizeKeepsTheSolidMark()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            InvokeKey(overlay, WF.Keys.P);

            // What a resize does: the buttons are thrown away and remade.
            typeof(FastQuickActionsWindow)
                .GetMethod("BuildLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, [bitmap, 144]);

            SD.Bitmap? icon = PinIcon(overlay);
            Assert.NotNull(icon);
            Assert.Same(SvgIcons.Get(Filled, icon!.Width, GlyphColor(overlay)), icon);
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
