using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinShot.Core;

/// <summary>
/// The WPF half of the window vocabulary, in one place because three windows were about to
/// grow their own copy: a shell that scales up from 92 % and fades in, the same move reversed
/// on the way out, and the slow breath that says a long operation is still running.
///
/// It works on a shell element inside the window rather than on the <see cref="Window"/>
/// itself: a Window's own RenderTransform is never applied, so the transform has to live on
/// the first child.
/// </summary>
internal static class WindowMotion
{
    /// <summary>Grows the shell from 92 % and fades it in. No-op with animations off, except
    /// to put the shell at rest — a shell left at Opacity 0 is an invisible window.</summary>
    public static void PlayEntrance(FrameworkElement? root, ScaleTransform? scale)
    {
        if (root is null || scale is null)
            return;

        if (!Motion.AnimationsEnabled())
        {
            root.Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
            return;
        }

        Duration duration = DurationFrom(root, "MotionEnter", Motion.EnterDurationMs);
        IEasingFunction ease = EaseFrom(root, "EaseOutCubic", EasingMode.EaseOut);

        root.Opacity = 0;
        scale.ScaleX = scale.ScaleY = 0.92;
        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, duration) { EasingFunction = ease });
        var grow = new DoubleAnimation(1, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>
    /// The entrance played backwards, then <paramref name="onDone"/> — quicker than the
    /// entrance (SwapMs against EnterDurationMs), because leaving should not make the user
    /// wait, and eased the exit way so it reads as the mirror. Calls back synchronously when
    /// animations are off, so a caller can always close in the completion.
    /// </summary>
    public static void PlayExit(FrameworkElement? root, ScaleTransform? scale, Action onDone)
    {
        if (root is null || scale is null || !Motion.AnimationsEnabled())
        {
            onDone();
            return;
        }

        Duration duration = DurationFrom(root, "MotionSwap", Motion.SwapMs);
        IEasingFunction ease = EaseFrom(root, "EaseInCubic", EasingMode.EaseIn);

        var shrink = new DoubleAnimation(0.96, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => onDone();
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>A slow breath on the control's own opacity: the one thing on screen that says
    /// work is still happening, without inventing a spinner the app has nowhere else.</summary>
    public static void StartWaitingPulse(UIElement element)
    {
        if (!Motion.AnimationsEnabled())
            return;

        var host = element as FrameworkElement;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.55,
            host?.TryFindResource("MotionPulse") as Duration? ?? new Duration(TimeSpan.FromMilliseconds(650)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = host?.TryFindResource("EaseInOutSine") as IEasingFunction
                             ?? new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    public static void StopWaitingPulse(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
    }

    private static Duration DurationFrom(FrameworkElement host, string key, int fallbackMs) =>
        host.TryFindResource(key) as Duration? ?? new Duration(TimeSpan.FromMilliseconds(fallbackMs));

    private static IEasingFunction EaseFrom(FrameworkElement host, string key, EasingMode mode) =>
        host.TryFindResource(key) as IEasingFunction ?? new CubicEase { EasingMode = mode };
}
