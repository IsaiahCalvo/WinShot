using System.Reflection;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The selector's entry into capture mode, as numbers.
///
/// What is being guarded: pressing the hotkey must dim the screen ONCE, smoothly, with no
/// brightness pop, no black flash and no double repaint. That used to be impossible by
/// construction — the overlay opened translucent painting solid BLACK and swapped to the
/// opaque dimmed snapshot when it arrived — and it was signed off on a screenshot, which is
/// exactly what a screenshot cannot show. So the composite is computed here instead:
/// a layered window at alpha a over a desktop D shows a*S + (1-a)*D, and that is the same
/// arithmetic DWM does, so a monotonic curve here is a monotonic curve on screen.
/// </summary>
[Collection("MotionRefcount")]
public class SelectorFreezeTests
{
    private const int Width = 240;
    private const int Height = 180;
    private static readonly SD.Rectangle Monitor = new(0, 0, Width, Height);

    /// <summary>A flat desktop, so one sample stands for the whole surface.</summary>
    private static readonly SD.Color Desktop = SD.Color.FromArgb(255, 180, 180, 184);

    // ---- frame-source selection (pure) ---------------------------------------------

    [Fact]
    public void AFrameInHandOpensOpaqueAndAColdDeviceOpensTransparent()
    {
        Assert.Equal(
            SelectorEntry.OpaqueFrozen,
            SelectorFreeze.ChooseEntry(freezeScreen: true, frozenInHand: true));
        Assert.Equal(
            SelectorEntry.TransparentUntilFrozen,
            SelectorFreeze.ChooseEntry(freezeScreen: true, frozenInHand: false));
    }

    /// <summary>
    /// Freeze switched off is a look the user chose, not a transient — it must never be
    /// confused with "the frame has not arrived yet", or turning freezing off would open an
    /// invisible overlay and then ramp to a wash it was told not to show.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TurningFreezeOffAlwaysGetsTheTranslucentWashWhateverTheCaptureDid(bool frozenInHand)
    {
        Assert.Equal(
            SelectorEntry.LiveTranslucent,
            SelectorFreeze.ChooseEntry(freezeScreen: false, frozenInHand));
    }

    [Fact]
    public void EachEntryOpensAtItsOwnAlpha()
    {
        Assert.Equal(255, SelectorFreeze.OpenAlpha(SelectorEntry.OpaqueFrozen));
        // Zero, not the old 45% black wash: a transparent surface composites to exactly the
        // live desktop, which is what makes the ramp that follows it artifact-free.
        Assert.Equal(0, SelectorFreeze.OpenAlpha(SelectorEntry.TransparentUntilFrozen));
        Assert.Equal(SelectorChrome.LiveAlpha, SelectorFreeze.OpenAlpha(SelectorEntry.LiveTranslucent));
    }

    [Fact]
    public void TheWarmGrabIsNotEvenAttemptedWhenFreezingIsOff()
    {
        // Not a mock: TryGrabBeforeShow must short-circuit on the setting before it reaches
        // the capture stack at all, so a freeze-off session never touches the GPU.
        Assert.Null(SelectorFreeze.TryGrabBeforeShow(freezeScreen: false));
    }

    // ---- the ramp itself ------------------------------------------------------------

    [Fact]
    public void TheRampCoversTheWholeRangeAndNeverGoesBackwards()
    {
        byte previous = SelectorFreeze.FadeAlpha(0d);
        Assert.Equal(0, previous);

        for (int i = 1; i <= 100; i++)
        {
            byte alpha = SelectorFreeze.FadeAlpha(Motion.EaseOutCubic(i / 100d));
            Assert.True(alpha >= previous, $"the ramp went backwards at {i}%: {previous} -> {alpha}");
            previous = alpha;
        }

        Assert.Equal(255, previous);
    }

    /// <summary>
    /// The numeric "no flash" check. Walks the ramp at the real frame clock, composites each
    /// frame the way DWM will, and demands that the dimmed ground only ever gets darker, that
    /// it starts at exactly the untouched desktop, and that no single frame moves more than a
    /// fraction of the whole change.
    ///
    /// The old entry fails all three: it began at 0.55*D (the black wash, a brightness DROP
    /// the instant the overlay appeared, before any freeze existed), then stepped to 0.549*D
    /// through whatever the paint ordering left behind — a black flash if the alpha flipped
    /// first, a brightening if the paint did.
    /// </summary>
    [Fact]
    public void TheDimmedGroundOnlyDarkens_AndNoSingleFrameCarriesMoreThanAQuarterOfIt()
    {
        using SD.Bitmap surface = PaintedSurface();
        SD.Color painted = surface.GetPixel(20, 20);

        int frames = Math.Max(2, SelectorFreeze.FadeDurationMs / Motion.FrameIntervalMs);
        var luminance = new List<double>(frames + 1);
        for (int i = 0; i <= frames; i++)
        {
            byte alpha = SelectorFreeze.FadeAlpha(Motion.EaseOutCubic(i / (double)frames));
            luminance.Add(ColorMath.RelativeLuminance(Composite(painted, Desktop, alpha)));
        }

        // Frame zero is the live desktop, untouched: nothing is on screen yet.
        Assert.Equal(ColorMath.RelativeLuminance(Desktop), luminance[0], 6);

        double total = luminance[0] - luminance[^1];
        Assert.True(total > 0.05, $"the ramp should visibly darken the desktop, moved {total:0.0000}");

        double worst = 0;
        for (int i = 1; i < luminance.Count; i++)
        {
            double step = luminance[i - 1] - luminance[i];
            Assert.True(step >= -0.0001,
                $"frame {i} got BRIGHTER ({luminance[i - 1]:0.0000} -> {luminance[i]:0.0000}) — that is the pop");
            worst = Math.Max(worst, step);
        }

        Assert.True(worst <= total * 0.25d,
            $"one frame carried {worst / total:P0} of the whole dim — that reads as a flash, not a fade");
    }

    /// <summary>
    /// The fast path's promise: with the frame already in hand there is no ramp at all, and
    /// the FIRST thing on screen is the finished image. So the composite at the opening alpha
    /// has to equal the settled surface exactly — one step, no intermediate state to see.
    /// </summary>
    [Fact]
    public void OpeningWithTheFrameInHandPutsTheFinishedImageOnScreenInOneStep()
    {
        using SD.Bitmap surface = PaintedSurface();
        SD.Color painted = surface.GetPixel(20, 20);

        byte alpha = SelectorFreeze.OpenAlpha(SelectorEntry.OpaqueFrozen);
        SD.Color first = Composite(painted, Desktop, alpha);

        Assert.Equal(painted.ToArgb(), first.ToArgb());
        // And it is a dim, not a blackout: the desktop is still legible underneath it.
        Assert.True(
            ColorMath.RelativeLuminance(first) < ColorMath.RelativeLuminance(Desktop),
            "the frozen ground should be darker than the live desktop");
        Assert.True(first.R > 40, $"the frozen ground should not be near-black, got {first}");
    }

    /// <summary>What DWM shows for a layered window at <paramref name="alpha"/> over a
    /// desktop. Rounded the way an 8-bit composite is.</summary>
    private static SD.Color Composite(SD.Color surface, SD.Color desktop, byte alpha)
    {
        double a = alpha / 255d;
        int Mix(int s, int d) => (int)Math.Round(a * s + (1 - a) * d);
        return SD.Color.FromArgb(
            255, Mix(surface.R, desktop.R), Mix(surface.G, desktop.G), Mix(surface.B, desktop.B));
    }

    /// <summary>The selector's ground as it is actually painted over a frozen flat desktop.</summary>
    private static SD.Bitmap PaintedSurface()
    {
        var frozen = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var fg = SD.Graphics.FromImage(frozen))
            fg.Clear(Desktop);

        var selector = new FastRegionSelectorDialog(
            () => Task.FromResult(new List<WindowInfo>()), settings: null);
        try
        {
            Set(selector, "_frozen", frozen);
            Set(selector, "_vs", Monitor);
            Set(selector, "_monitorBounds", Monitor);
            Set(selector, "_currentScreen", new SD.Point(Width - 1, Height - 1));
            Set(selector, "_magnifierOn", false);

            var surface = new SD.Bitmap(Width, Height, SD.Imaging.PixelFormat.Format32bppArgb);
            using var g = SD.Graphics.FromImage(surface);
            selector.PaintSurface(g, Monitor);
            return surface;
        }
        finally
        {
            selector.Dispose(); // takes the frozen bitmap with it, as a real session does
        }
    }

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
}
