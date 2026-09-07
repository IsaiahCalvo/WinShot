using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// What counts as "in the corner stack", and what happens at its edges. The stack cap, the
/// batch menu rows and the gate that offers them used to disagree about it — the menu hid
/// Close all because a card was off screen behind the annotate editor, and then Close all
/// closed that card anyway and the annotation went with it.
/// </summary>
public class QuickAccessOverlayStackTests
{
    /// <summary>
    /// The cap has to be computed from the card's real device size. It ran in the
    /// constructor, where _dpi is still 96 and Size is still the 96-dpi card, so on a scaled
    /// monitor it divided the working area by a 132 px step instead of a 264 px one and let
    /// roughly twice the cards in — which Place then clamped onto one rectangle, the pile-up
    /// the cap exists to prevent.
    /// </summary>
    [Fact]
    public void TheStackNeverHoldsMoreCardsThanFitAtTheCardsRealDpi()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            var made = new List<FastQuickActionsWindow>();
            try
            {
                for (int i = 0; i < 7; i++)
                {
                    var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                    made.Add(card);
                    card.Show();
                    WF.Application.DoEvents();
                }
                PumpUntil(() => Motion.Holders == 0, 6000);

                var alive = made.Where(c => !IsGone(c)).ToList();
                Assert.NotEmpty(alive);
                FastQuickActionsWindow probe = alive[^1];
                int dpi = (int)Field(probe, "_dpi")!;
                int capacity = QuickAccessOverlayLayout.StackCapacity(
                    (SD.Rectangle)Field(probe, "_placedWorkingArea")!,
                    probe.Size,
                    settings.Current.OverlayPosition,
                    dpi);
                Assert.True(
                    alive.Count <= capacity,
                    $"{alive.Count} cards stayed in a stack that only fits {capacity} at {dpi} dpi");
            }
            finally
            {
                DisposeAll(made);
            }
        });
    }

    /// <summary>
    /// A card the annotate editor is holding off screen is not in the stack, so the cap must
    /// not evict it. It used to: the eviction closed the card, the editor's ImageCommitted
    /// then landed on a closed card, the flattened bitmap was disposed, and the annotation
    /// was gone with no message at all.
    /// </summary>
    [Fact]
    public void ABurstOfCapturesDoesNotEvictTheCardTheEditorIsHolding()
    {
        RunSta(() => WithHeldCard((held, settings, made) =>
        {
            for (int i = 0; i < 7; i++)
            {
                var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                made.Add(card);
                card.Show();
                WF.Application.DoEvents();
            }
            PumpUntil(() => Motion.Holders == 0, 6000);
            Assert.False(IsGone(held), "the stack cap evicted the card the editor was holding");
        }));
    }

    /// <summary>
    /// The menu hides Save all / Close all unless more than one card is actually in the
    /// stack, so the handlers must act on exactly that set.
    /// </summary>
    [Fact]
    public void CloseAllDoesNotReachTheCardTheEditorIsHolding()
    {
        RunSta(() => WithHeldCard((held, settings, made) =>
        {
            var visible = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            made.Add(visible);
            visible.Show();
            WF.Application.DoEvents();

            Invoke(visible, "CloseEveryCard");
            PumpUntil(() => Motion.Holders == 0, 6000);
            Assert.False(IsGone(held), "Close all reached the card the editor was holding off screen");
        }));
    }

    /// <summary>
    /// "Temporarily hide" and "the editor has it" are different states. Flagging the second
    /// as the first made the next capture pop the card back on screen mid-edit.
    /// </summary>
    [Fact]
    public void TemporarilyHideDoesNotReviveTheCardTheEditorIsHolding()
    {
        RunSta(() => WithHeldCard((held, settings, made) =>
        {
            var visible = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            made.Add(visible);
            visible.Show();
            WF.Application.DoEvents();

            Invoke(visible, "HideEveryCard");
            PumpUntil(() => Motion.Holders == 0, 3000);

            var next = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            made.Add(next);
            next.Show();
            PumpUntil(() => Motion.Holders == 0, 6000);

            Assert.False(held.Visible, "the editor's card was popped back on screen while the editor still had it");
        }));
    }

    /// <summary>
    /// Stepping aside is a transition, not a cut, and coming back mirrors it: the card fades
    /// where it stands rather than replaying the arrival slide it never left.
    /// </summary>
    [Fact]
    public void SteppingAsideAndComingBackAreAnimatedAndSymmetric()
    {
        RunSta(() =>
        {
            var previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                using var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), new SettingsService());
                card.Show();
                PumpUntil(() => Motion.Holders == 0, 6000);
                SD.Point settled = card.Location;

                card.StepAside();
                Assert.True(card.Visible, "the card cut off screen instead of fading");
                PumpUntil(() => card.Opacity < 1d, 1000);
                Assert.True(card.Opacity < 1d, "nothing was animating on the way out");
                PumpUntil(() => !card.Visible, 3000);
                Assert.False(card.Visible);

                card.ComeBack();
                Assert.True(card.Visible);
                Assert.True(card.Opacity < 1d, "the card came back at full strength instead of fading in");
                Assert.Equal(settled, card.Location);
                PumpUntil(() => Motion.Holders == 0, 6000);
                Assert.Equal(1d, card.Opacity, 3);
                Assert.Equal(settled, card.Location);
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                WF.Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// A status message wears the hover chrome, so the pointer arriving while one is up has
    /// to be a no-op. Forcing hover in the paint alone left the tween at zero, and the frame
    /// after MouseEnter dropped the blur and the tint entirely before ramping them back.
    /// </summary>
    [Fact]
    public void ThePointerArrivingOnAFlashingCardDoesNotUnblurIt()
    {
        RunSta(() =>
        {
            var previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                using var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), new SettingsService());
                card.Show();
                PumpUntil(() => Motion.Holders == 0, 6000);

                card.FlashStatus("Text copied");
                Assert.Equal(1d, (double)Field(card, "_hoverAmount")!, 3);

                Invoke(card, "SetHovering", true);
                Assert.Equal(1d, (double)Field(card, "_hoverAmount")!, 3);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                WF.Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// Closing the card in the middle of the step-aside fade. Motion.Run holds the
    /// process-wide high-resolution clock for its whole life, so a tween whose owner is
    /// disposed out from under it leaks timeBeginPeriod(1) for as long as the app runs.
    /// </summary>
    [Fact]
    public void DisposingACardMidFadeReleasesTheMotionClock()
    {
        RunSta(() =>
        {
            var previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            try
            {
                var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), new SettingsService());
                card.Show();
                PumpUntil(() => Motion.Holders == 0, 6000);

                card.StepAside();
                PumpUntil(() => Motion.Holders > 0, 1000);
                Assert.True(Motion.Holders > 0, "the step-aside fade never started");

                card.Dispose();
                WF.Application.DoEvents();
                PumpUntil(() => Motion.Holders == 0, 2000);
                Assert.Equal(0, Motion.Holders);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                WF.Application.DoEvents();
            }
        });
    }

    // ---------------------------------------------------------------- helpers

    private static void WithHeldCard(
        Action<FastQuickActionsWindow, SettingsService, List<FastQuickActionsWindow>> body)
    {
        var settings = new SettingsService();
        var made = new List<FastQuickActionsWindow>();
        try
        {
            var held = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            made.Add(held);
            held.Show();
            WF.Application.DoEvents();
            held.StepAside(); // exactly what opening the annotate editor does
            PumpUntil(() => !held.Visible, 3000);
            body(held, settings, made);
        }
        finally
        {
            DisposeAll(made);
        }
    }

    private static void DisposeAll(List<FastQuickActionsWindow> cards)
    {
        foreach (var card in cards)
        {
            try { card.Dispose(); }
            catch { /* a card the cap already closed */ }
        }
        WF.Application.DoEvents();
    }

    private static bool IsGone(FastQuickActionsWindow card)
        => card.IsDisposed || (bool)Field(card, "_closed")!;

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Invoke(object target, string method, params object[] args)
        => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            WF.Application.DoEvents();
            Thread.Sleep(5);
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
