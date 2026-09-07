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

    /// <summary>How far past the target an eased amount may carry the rect. Comfortably above
    /// EaseOutBack's ~1.10 peak and low enough that nonsense stays on screen.</summary>
    private const float MaxOvershoot = 1.2f;

    private readonly Stopwatch _clock = new();
    private SD.RectangleF _from;
    private SD.RectangleF _to;

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
        Current = SD.Rectangle.Round(_from);
        IsActive = true;
        _clock.Restart();
    }

    public void Stop()
    {
        IsActive = false;
        _clock.Reset();
    }

    /// <summary>Advances to the current wall-clock position. Returns whether it is still running.</summary>
    public bool Update() =>
        // Clamped, because the overload below ends only on an exact 1.0 so that an eased
        // caller may overshoot past it. This one owns a plain linear clock with no overshoot
        // in it, and a late frame lands past the duration routinely.
        Update(Duration.Ticks <= 0 ? 1f : Math.Min(1f, (float)_clock.Elapsed.Ticks / Duration.Ticks));

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
        Current = SD.Rectangle.Round(new SD.RectangleF(
            Lerp(_from.X, _to.X, amount),
            Lerp(_from.Y, _to.Y, amount),
            Lerp(_from.Width, _to.Width, amount),
            Lerp(_from.Height, _to.Height, amount)));
        return true;
    }

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
