using System.Diagnostics;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Slides a rectangle from one position/size to another over a fixed duration, so the hover
/// highlight glides between elements instead of teleporting.
///
/// Behaviour modelled on ShareX (GPL-3) — no code copied. What was taken is the observable
/// rule, not an implementation: a plain linear lerp of X/Y/W/H, and a retarget mid-flight that
/// starts from wherever the rect currently is rather than from the old target, because
/// otherwise a fast mouse sweep makes the highlight jump backwards before catching up.
/// See THIRD_PARTY_NOTICES.md.
///
/// Time is read from a stopwatch rather than counted in frames, so a dropped repaint shortens
/// the animation instead of stretching it.
/// </summary>
internal sealed class RectangleTween
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(WinShot.Core.Motion.GlideDurationMs);

    /// <summary>One animation frame, the budget <see cref="Update()"/> advances on.</summary>
    private static readonly long FrameTicks =
        TimeSpan.FromMilliseconds(WinShot.Core.Motion.FrameIntervalMs).Ticks;

    /// <summary>How far past the target an eased amount may carry the rect. Comfortably above
    /// EaseOutBack's ~1.10 peak and low enough that nonsense stays on screen.</summary>
    private const float MaxOvershoot = 1.2f;

    private readonly Stopwatch _clock = new();
    private SD.RectangleF _from;
    private SD.RectangleF _to;

    /// <summary>Elapsed time at the last advance <see cref="Update()"/> actually performed, and
    /// what that advance answered. See <see cref="Update()"/> for why they are kept.</summary>
    private long _advancedTicks = -1;
    private bool _advancedRunning;

    public bool IsActive { get; private set; }

    /// <summary>Interpolated rectangle for the current frame; meaningless unless active.</summary>
    public SD.Rectangle Current { get; private set; }

    /// <summary>Begins (or redirects) a glide toward <paramref name="to"/>.</summary>
    public void Retarget(SD.Rectangle from, SD.Rectangle to)
    {
        // Mid-flight: keep the on-screen position as the new origin. Below a couple of pixels
        // the in-flight rect is degenerate and gliding out of it looks like a glitch, so fall
        // back to the caller's origin — don't "tidy up" the threshold.
        _from = IsActive && Current.Width > 2 && Current.Height > 2 ? Current : from;
        _to = to;

        // Animation turned off system-wide (Settings > Accessibility > Visual effects) means
        // the highlight LANDS, the way every Motion.Run tween in the app already does. This one
        // reads its own stopwatch instead of going through Motion.Run, so it had been ignoring
        // the setting outright — and round 2 widened that from the window-to-window sweep to
        // the entrance and the exit as well, which is the whole highlight.
        if (!WinShot.Core.Motion.AnimationsEnabled())
        {
            Current = SD.Rectangle.Round(_to);
            Stop();
            return;
        }

        Current = RoundEdges(_from.Left, _from.Top, _from.Right, _from.Bottom);
        IsActive = true;
        _clock.Restart();
        // The clock went back to zero, so the frame budget in Update() has to as well — held
        // against a stamp from the old flight it would refuse to advance the first frame or
        // two of the new one, which is a stall at the exact moment the pointer is moving.
        _advancedTicks = -1;
    }

    public void Stop()
    {
        IsActive = false;
        _clock.Reset();
        _advancedTicks = -1;
    }

    /// <summary>
    /// Advances to the current wall-clock position, at most ONCE per animation frame. Returns
    /// whether it is still running.
    ///
    /// The frame budget is the reason for the "at most once". The selector paints one surface
    /// per monitor and every one of them calls this, so on a multi-monitor desktop the second
    /// surface used to advance the glide again 1-3 ms after the first: the two halves of one
    /// border straddling a seam were computed from two different moments and landed a pixel
    /// apart for that frame, all the way across. Holding the answer for the rest of the frame
    /// means every surface in a paint round draws the same rectangle, which is the only way a
    /// highlight spanning two displays can be one object.
    ///
    /// It is also the whole point of <see cref="WinShot.Core.Motion.FrameIntervalMs"/>. The
    /// glide rides the paint loop, which round 1 measured at 1.4 ms a frame — so it was
    /// advancing ~140 times over a 200 ms glide's 25 intended frames, burning repaints on
    /// sub-pixel moves that round to the same rect.
    /// </summary>
    public bool Update()
    {
        if (!IsActive)
            return false;

        long ticks = _clock.Elapsed.Ticks;
        if (_advancedTicks >= 0 && ticks - _advancedTicks < FrameTicks)
            return _advancedRunning;

        _advancedTicks = ticks;
        // Clamped, because the overload below ends only on an exact 1.0 so that an eased
        // caller may overshoot past it. This one owns a plain linear clock with no overshoot
        // in it, and a late frame lands past the duration routinely.
        _advancedRunning = Update(Duration.Ticks <= 0 ? 1f : Math.Min(1f, (float)ticks / Duration.Ticks));
        return _advancedRunning;
    }

    /// <summary>
    /// Advances to an explicit amount, for callers that already own the clock and the curve
    /// (<see cref="WinShot.Core.Motion.Run"/>). Without this the caller's ease was dead code:
    /// it handed in EaseOutCubic and the rect kept reading its own stopwatch through a plain
    /// lerp, so the pill glided at constant velocity while everything else eased.
    ///
    /// The end is the caller's exact 1.0 and nothing else, which is what
    /// <see cref="WinShot.Core.Motion.Run"/> delivers on its last frame. An eased amount above
    /// 1 is a deliberate overshoot — EaseOutBack sails past the target and settles back — and
    /// treating that as the end would snap the rect home partway through the very curve the
    /// caller reached for.
    /// </summary>
    public bool Update(float amount)
    {
        if (!IsActive)
            return false;

        if (amount == 1f)
        {
            Current = SD.Rectangle.Round(_to);
            Stop();
            return false;
        }

        // Bounded, not clamped to 1: an ease is allowed past the target and nothing else is.
        // EaseOutBack peaks near 1.10, so the ceiling leaves the whole curve room while a
        // garbage amount (infinity, from a divide by a zero duration) still cannot round a
        // coordinate to int.MinValue.
        amount = Math.Clamp(float.IsNaN(amount) ? 0f : amount, 0f, MaxOvershoot);
        Current = RoundEdges(
            Lerp(_from.Left, _to.Left, amount),
            Lerp(_from.Top, _to.Top, amount),
            Lerp(_from.Right, _to.Right, amount),
            Lerp(_from.Bottom, _to.Bottom, amount));
        return true;
    }

    /// <summary>
    /// Each EDGE rounded to its own device pixel, rather than rounding the position and the
    /// size and letting the right and bottom edges inherit both errors.
    ///
    /// Measured, not reasoned about: a scripted 200 ms sweep logged per-frame edge deltas of
    /// 10,8,10,8,10 on the right edge against a steady 10,10,10 on the left, because
    /// Rectangle.Round(x) + Rectangle.Round(w) stacks two independent ±0.5 px roundings on
    /// the same edge. That is a 2 px wobble on one side of a border whose other side is
    /// perfectly even, at ~120 frames a second — the shimmer. Rounding the edges themselves
    /// halves it to the 1 px that whole-pixel rendering cannot avoid.
    /// </summary>
    private static SD.Rectangle RoundEdges(float left, float top, float right, float bottom)
    {
        int l = (int)MathF.Round(left);
        int t = (int)MathF.Round(top);
        // Never inverted: a degenerate in-flight rect would paint a border inside out.
        return SD.Rectangle.FromLTRB(l, t, Math.Max(l, (int)MathF.Round(right)), Math.Max(t, (int)MathF.Round(bottom)));
    }

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
