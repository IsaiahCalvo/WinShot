using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// The dialog's entrance shipped with nothing covering it at all. What is checked here is the
/// state Enter() leaves behind, not the pixels a clock would produce: no window is shown, so
/// nothing depends on a WPF render tick that only exists when this assembly's other test
/// classes happen to have left a pump running.
/// </summary>
public class ThemedMessageDialogTests
{
    [Fact]
    public void EntranceStartsHiddenAndAnimatesTheWholeCard() => WithAnimations(true, () => RunOnSta(() =>
    {
        var (dialog, body, footer) = ThemedMessageDialog.Build(null, "Title", "Body");
        ThemedMessageDialog.AddButton(footer, "OK", true, () => { });

        // Hidden and 2% down until Loaded, and it is the CONTENT that scales — a transform on
        // the Window shrinks its content inside a frame that does not move with it.
        Assert.Equal(0d, body.Opacity);
        var scale = (ScaleTransform)body.RenderTransform;
        Assert.Equal(0.98d, scale.ScaleX);
        Assert.Equal(0.98d, scale.ScaleY);

        RaiseLoaded(dialog);

        Assert.True(body.HasAnimatedProperties, "the content never got its fade");
        Assert.True(scale.HasAnimatedProperties, "the content never got its grow");
    }));

    /// <summary>The hotkey dialogs call Build() and then add their own rows. Enter() has
    /// already run by then, so the entrance has to be on the container rather than on the
    /// children it happened to find in it.</summary>
    [Fact]
    public void ContentAppendedAfterBuildComesInWithTheDialog() => WithAnimations(true, () => RunOnSta(() =>
    {
        var (dialog, body, footer) = ThemedMessageDialog.Build(null, "Title", "Body");
        var extra = new TextBlock { Text = "appended later" };
        body.Children.Insert(body.Children.Count - 1, extra);
        ThemedMessageDialog.AddButton(footer, "Close", false, () => { });

        RaiseLoaded(dialog);

        Assert.Contains(extra, body.Children.Cast<UIElement>());
        Assert.True(body.HasAnimatedProperties, "the entrance is not on the panel the child was added to");
        Assert.False(extra.HasAnimatedProperties, "the child should ride the panel, not carry its own animation");
    }));

    /// <summary>
    /// With no Application there is no merged theme, so MotionEnter does not resolve and the
    /// entrance falls back to its literal duration. That fallback never consulted the
    /// preference — the one motion path in the app that inferred the setting from a resource
    /// instead of asking Motion for it — so a user with animation effects off got a dialog
    /// that faded and grew anyway.
    /// </summary>
    [Fact]
    public void AnimationOffLeavesTheDialogVisibleAndUnanimated() => WithAnimations(false, () => RunOnSta(() =>
    {
        var (dialog, body, footer) = ThemedMessageDialog.Build(null, "Save failed", "Body");
        ThemedMessageDialog.AddButton(footer, "OK", true, () => { });

        // Not "ends visible": there is no hidden first frame and no transform to undo.
        Assert.Equal(1d, body.Opacity);
        Assert.Null(body.RenderTransform as ScaleTransform);

        RaiseLoaded(dialog);

        Assert.Equal(1d, body.Opacity);
        Assert.False(body.HasAnimatedProperties, "animation-off still animated the entrance");
    }));

    /// <summary>Runs the entrance without showing a window: Enter() hangs its work off Loaded,
    /// and the routed event can be raised directly.</summary>
    private static void RaiseLoaded(Window dialog) =>
        dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, dialog));

    private static void WithAnimations(bool enabled, Action body)
    {
        Func<bool> previous = Motion.AnimationsEnabled;
        Motion.AnimationsEnabled = () => enabled;
        try { body(); }
        finally { Motion.AnimationsEnabled = previous; }
    }

    private static void RunOnSta(Action action)
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
