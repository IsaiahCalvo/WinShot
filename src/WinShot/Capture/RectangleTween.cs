using System.Diagnostics;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Slides a rectangle from one position/size to another over a fixed duration, so the hover
/// highlight glides between elements instead of teleporting.
///
/// Port of ShareX's RectangleAnimation (GPL-3, ShareX.ScreenCaptureLib): a plain linear lerp
/// of X/Y/W/H over 200 ms. Retargeting mid-flight starts from wherever the rect currently is,
/// never from the old target — otherwise a fast mouse sweep makes the highlight jump backwards
/// before catching up.
///
/// Time is read from a stopwatch rather than counted in frames, so a dropped repaint shortens
/// the animation instead of stretching it.
/// </summary>
internal sealed class RectangleTween
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(200);

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
    public bool Update()
    {
        if (!IsActive)
            return false;

        float amount = Duration.Ticks <= 0 ? 1f : (float)_clock.Elapsed.Ticks / Duration.Ticks;
        if (amount >= 1f)
        {
            Current = SD.Rectangle.Round(_to);
            Stop();
            return false;
        }

        Current = SD.Rectangle.Round(new SD.RectangleF(
            Lerp(_from.X, _to.X, amount),
            Lerp(_from.Y, _to.Y, amount),
            Lerp(_from.Width, _to.Width, amount),
            Lerp(_from.Height, _to.Height, amount)));
        return true;
    }

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
