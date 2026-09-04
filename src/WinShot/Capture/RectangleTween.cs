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

    private readonly Stopwatch _clock = new();
    private SD.RectangleF _from;
    private SD.RectangleF _to;

    public bool IsActive { get; private set; }

    /// <summary>Interpolated rectangle for the current frame; meaningless unless active.</summary>
    public SD.Rectangle Current { get; private set; }

    /// <summary>Begins (or redirects) a glide toward <paramref name="to"/>.</summary>
    public void Retarget(SD.Rectangle from, SD.Rectangle to)
    {
        // Mid-flight: keep the on-screen position as the new origin.
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
        Update(Duration.Ticks <= 0 ? 1f : (float)_clock.Elapsed.Ticks / Duration.Ticks);

    /// <summary>
    /// Advances to an explicit amount, for callers that already own the clock and the curve
    /// (<see cref="WinShot.Core.Motion.Run"/>). Without this the caller's ease was dead code:
    /// it handed in EaseOutCubic and the rect kept reading its own stopwatch through a plain
    /// lerp, so the pill glided at constant velocity while everything else eased.
    /// </summary>
    public bool Update(float amount)
    {
        if (!IsActive)
            return false;

        if (amount >= 1f)
        {
            Current = SD.Rectangle.Round(_to);
            Stop();
            return false;
        }

        amount = Math.Max(0f, amount);
        Current = SD.Rectangle.Round(new SD.RectangleF(
            Lerp(_from.X, _to.X, amount),
            Lerp(_from.Y, _to.Y, amount),
            Lerp(_from.Width, _to.Width, amount),
            Lerp(_from.Height, _to.Height, amount)));
        return true;
    }

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
