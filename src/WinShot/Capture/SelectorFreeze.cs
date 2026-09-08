using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

/// <summary>How a selector overlay's surfaces first reach the screen.</summary>
internal enum SelectorEntry
{
    /// <summary>The freeze frame was already in hand before the first paint: the surfaces
    /// open opaque over the dimmed snapshot. Nothing transitions, because there is nothing
    /// to transition FROM — the screen simply dims once, on every monitor in the same
    /// message-pump turn.</summary>
    OpaqueFrozen,

    /// <summary>No frame yet. The surfaces open fully transparent — showing the live desktop
    /// untouched, not a black wash — and ramp to the dimmed snapshot when it lands.</summary>
    TransparentUntilFrozen,

    /// <summary>Freeze is switched off in settings, so the overlay is a translucent black
    /// wash over a live desktop for the whole session. This is a look the user chose, not a
    /// transient state.</summary>
    LiveTranslucent,
}

/// <summary>
/// The selector's freeze frame: where it comes from, and how it arrives on screen.
///
/// ROOT CAUSE of the entry flicker this replaces. The overlay used to open translucent over
/// the live desktop and paint solid BLACK at 45% alpha, then swap to the opaque dimmed
/// snapshot 170-555 ms later when Windows Graphics Capture finally produced a frame. Two
/// separate problems fed that:
///
///  1. The freeze was slow because the source was wrong. Every capture went to WGC first, and
///     WGC's device is torn down after each capture on purpose (a live session draws Windows'
///     yellow capture border), so every freeze paid its 100-460 ms setup again. Desktop
///     Duplication draws no border, so its device may stay warm — and warm it answers in
///     13 ms, which is inside the budget for grabbing the frame BEFORE the first paint.
///     That removes the transition entirely rather than smoothing it.
///
///  2. The fallback was unfixable as written. A translucent surface painting black composites
///     to a*0 + (1-a)*D = 0.55*D, and the frozen surface paints 0.549*D; the two are equal
///     only in the steady state, so ANY swap between them shows an artifact, and every
///     ordering of paint-then-flip was just choosing which artifact. The way out is not a
///     better ordering — it is to stop painting black. A surface at alpha 0 composites to
///     exactly the live desktop, so ramping it to the dimmed snapshot is monotonic darkening
///     from 1.0*D to 0.549*D with no step and no pop at either end.
///
/// So: warm grab first (no transition at all), transparent-and-ramp when the device is cold
/// (one deliberate transition), and the old translucent-black wash kept only for the users
/// who turned freezing off, where it is the intended steady state.
/// </summary>
internal static class SelectorFreeze
{
    /// <summary>Layered-window alpha of a fully transparent surface — the live desktop,
    /// untouched. Not "invisible chrome": the surface still hit-tests, so a drag started
    /// during the ramp is not lost.</summary>
    public const byte TransparentAlpha = 0;

    /// <summary>Layered-window alpha of a surface showing its own dimmed frozen slice.</summary>
    public const byte FrozenAlpha = 255;

    /// <summary>The ramp from live to frozen. One surface arriving on screen for the first
    /// time — the vocabulary's own name for exactly this — rather than a new number.</summary>
    public const int FadeDurationMs = Motion.EnterDurationMs;

    /// <summary>
    /// Which entry a session gets. Pure: the two inputs are the user's freeze setting and
    /// whether the warm grab produced a frame.
    /// </summary>
    public static SelectorEntry ChooseEntry(bool freezeScreen, bool frozenInHand)
    {
        if (!freezeScreen)
            return SelectorEntry.LiveTranslucent;
        return frozenInHand ? SelectorEntry.OpaqueFrozen : SelectorEntry.TransparentUntilFrozen;
    }

    /// <summary>The layered alpha a surface is unparked at for a given entry.</summary>
    public static byte OpenAlpha(SelectorEntry entry) => entry switch
    {
        SelectorEntry.OpaqueFrozen => FrozenAlpha,
        SelectorEntry.TransparentUntilFrozen => TransparentAlpha,
        _ => SelectorChrome.LiveAlpha,
    };

    /// <summary>Whether the entry's surfaces are opaque snapshots rather than a wash.</summary>
    public static bool IsOpaque(SelectorEntry entry) => entry == SelectorEntry.OpaqueFrozen;

    /// <summary>
    /// The alpha at a point in the live-to-frozen ramp. Pure, and monotonic in
    /// <paramref name="eased"/> — the "no flash" assertion is exactly this property, so it is
    /// worth having somewhere a test can reach without a window.
    /// </summary>
    public static byte FadeAlpha(double eased) =>
        (byte)Math.Clamp((int)Math.Round(eased * FrozenAlpha), TransparentAlpha, FrozenAlpha);

    /// <summary>
    /// Tries to have the freeze frame in hand before the overlay is shown. Null means the
    /// duplication device was cold, and the caller opens transparent and ramps instead.
    /// </summary>
    public static SD.Bitmap? TryGrabBeforeShow(bool freezeScreen) =>
        freezeScreen ? CaptureService.TryCaptureFreezeFrameWarm() : null;

    /// <summary>
    /// Puts every surface at the entry's opening alpha in one pass, coordinator first.
    /// Pooled surfaces may still be opaque from a previous frozen session, so this is a set,
    /// never a nudge.
    /// </summary>
    public static void Unpark(WF.Form coordinator, IEnumerable<WF.Form> panes, SelectorEntry entry)
    {
        // SetLayeredAlpha only, never Form.Opacity: crossing the 1.0 boundary makes WinForms
        // restyle WS_EX_LAYERED, which recreates a full-screen TopMost handle at a measured
        // cost of 150-500 ms. The surfaces are born translucent in their constructors for
        // exactly this reason and are never restyled again.
        byte alpha = OpenAlpha(entry);
        SelectorSurfaces.Unpark(coordinator, alpha);
        foreach (var pane in panes)
            SelectorSurfaces.Unpark(pane, alpha);
    }

    /// <summary>
    /// Ramps the surfaces from transparent to the dimmed frozen frame. Every surface is
    /// painted first and then moved together on each frame, so the monitors darken in step
    /// instead of one chasing another.
    ///
    /// Returns the tween handle (null when animations are off system-wide, in which case the
    /// end state has already been applied inline).
    /// </summary>
    public static IDisposable? FadeToFrozen(WF.Form coordinator, IReadOnlyList<WF.Form> panes)
    {
        var live = new List<WF.Form>(panes.Count + 1);
        if (!coordinator.IsDisposed && coordinator.IsHandleCreated)
            live.Add(coordinator);
        foreach (var pane in panes)
        {
            if (!pane.IsDisposed && pane.IsHandleCreated)
                live.Add(pane);
        }

        // Paint the frozen ground into every back buffer BEFORE the first frame moves any
        // alpha. At alpha 0 none of it is on screen yet, so this costs the ramp nothing and
        // guarantees the first visible frame is already the finished image.
        foreach (var form in live)
        {
            form.Invalidate();
            form.Update();
        }

        return Motion.Run(
            FadeDurationMs,
            Motion.EaseOutCubic,
            progress =>
            {
                byte alpha = FadeAlpha(progress);
                foreach (var form in live)
                {
                    if (!form.IsDisposed && form.IsHandleCreated)
                        WinShot.Scrolling.CaptureExclusion.SetLayeredAlpha(form.Handle, alpha);
                }
            });
    }
}
