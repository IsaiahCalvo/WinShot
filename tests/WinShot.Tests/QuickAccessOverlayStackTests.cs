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

    /// <summary>
    /// The editor round trip must not leave a hole in the column while it runs, or drop the
    /// returning card on top of a survivor when it ends. StepAside only set a flag and faded,
    /// so the cards above kept their slots for the whole editor session — and ComeBack then
    /// derived its slot from a COUNT of what was still in the corner, which with one other
    /// card on screen is exactly the rectangle that card is sitting in.
    /// </summary>
    [Fact]
    public void ACardSteppingAsideClosesItsGapAndComesBackToAFreeSlot()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            var made = new List<FastQuickActionsWindow>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var made_i = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                    made.Add(made_i);
                    made_i.Show();
                    WF.Application.DoEvents();
                }
                PumpUntil(() => Motion.Holders == 0, 6000);
                FastQuickActionsWindow first = made[0];
                FastQuickActionsWindow second = made[1];
                SD.Point anchored = first.Location;
                Assert.NotEqual(anchored, second.Location);

                first.StepAside(); // exactly what opening the annotate editor does
                PumpUntil(() => !first.Visible, 3000);
                PumpUntil(() => Motion.Holders == 0, 6000);
                Assert.Equal(anchored, second.Location);

                first.ComeBack();
                PumpUntil(() => Motion.Holders == 0, 6000);
                Assert.NotEqual(second.Location, first.Location);
                Assert.False(
                    first.Bounds.IntersectsWith(second.Bounds),
                    $"the returning card at {first.Bounds} landed on the card at {second.Bounds}");
            }
            finally
            {
                DisposeAll(made);
            }
        });
    }

    /// <summary>
    /// StepAside remembers the card's resting opacity so ComeBack has something to fade back
    /// TO. Reading it unconditionally meant a second gesture landing inside the 140 ms return
    /// fade — re-annotating, or "Temporarily hide" over a card still coming back — recorded
    /// whatever the fade had reached, and the card settled at 0.23 for the life of the
    /// process on the one surface that is explicitly pinned.
    /// </summary>
    [Fact]
    public void SteppingAsideInsideTheReturnFadeDoesNotDimTheCardForGood()
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
                Assert.Equal(1d, card.Opacity, 3);

                card.StepAside();
                PumpUntil(() => !card.Visible, 3000);
                card.ComeBack();
                PumpUntil(() => card.Opacity > 0.2d && card.Opacity < 0.9d, 1000);

                card.StepAside();
                PumpUntil(() => !card.Visible, 3000);
                card.ComeBack();
                PumpUntil(() => Motion.Holders == 0, 6000);

                Assert.Equal(1d, card.Opacity, 3);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                WF.Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// The exit takes over every other tween on the card, the return fade included. It did
    /// not, so a card evicted by the stack cap while it was fading back in had two loops
    /// writing Opacity every frame and visibly brightened back to full strength in the middle
    /// of flying out. Machine-timed: the next capture revives the hidden cards and then trims
    /// the oldest, with no human reaction speed involved.
    /// </summary>
    [Fact]
    public void ACardClosedInsideTheReturnFadeOnlyEverGetsDimmer()
    {
        RunSta(() =>
        {
            var previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), new SettingsService());
            try
            {
                card.Show();
                PumpUntil(() => Motion.Holders == 0, 6000);

                card.StepAside();
                PumpUntil(() => !card.Visible, 3000);
                card.ComeBack();
                PumpUntil(() => card.Opacity > 0.15d, 1000);

                card.Close(); // exactly what the stack cap does to the oldest card
                double brightest = card.Opacity;
                var deadline = DateTime.UtcNow.AddMilliseconds(1500);
                while (!card.IsDisposed && DateTime.UtcNow < deadline)
                {
                    WF.Application.DoEvents();
                    if (card.IsDisposed || !card.Visible)
                        break;
                    Assert.True(
                        card.Opacity <= brightest + 0.02d,
                        $"the flying-out card brightened from {brightest:0.000} back to {card.Opacity:0.000}");
                    brightest = Math.Min(brightest, card.Opacity);
                    Thread.Sleep(2);
                }
            }
            finally
            {
                try { card.Dispose(); } catch { /* the exit flight already closed it */ }
                Motion.AnimationsEnabled = previous;
                PumpUntil(() => Motion.Holders == 0, 6000);
            }
        });
    }

    /// <summary>
    /// "Temporarily hide" steps every card aside, and each StepAside reflows the cards still
    /// visible — so every hidden card goes off screen with a 280 ms restack tween still aimed
    /// at the slot the collapsing column handed it, and nothing stops a WF.Timer when Visible
    /// goes false. A capture taken inside those 280 ms revived the stack, placed each card on
    /// its own slot, and then had the stale tween write all of them back onto one rectangle a
    /// frame later — permanently, invisibly, with only the topmost card clickable. The window
    /// is a hotkey pressed within about a third of a second of clicking the row.
    /// </summary>
    [Fact]
    public void AStackHiddenAndRevivedInsideItsOwnRestackDoesNotPileUp()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            var made = new List<FastQuickActionsWindow>();
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                    made.Add(card);
                    card.Show();
                    WF.Application.DoEvents();
                }
                PumpUntil(() => Motion.Holders == 0, 6000);

                Invoke(made[^1], "HideEveryCard");
                PumpUntil(() => made.All(c => !c.Visible), 3000);
                // No pump to idle here: the restack tween the hide started is still running,
                // which is the whole repro.
                var fresh = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                made.Add(fresh);
                fresh.Show();
                PumpUntil(() => Motion.Holders == 0, 6000);

                var seen = new HashSet<SD.Point>();
                foreach (var card in made.Where(c => !IsGone(c)))
                    Assert.True(
                        seen.Add(card.Location),
                        $"two revived cards landed on {card.Location}: " +
                        string.Join(", ", made.Select(c => c.Location)));
            }
            finally
            {
                DisposeAll(made);
            }
        });
    }

    /// <summary>
    /// "Temporarily hide" flagged every card in StackedCards(), which does not filter on
    /// Visible — so a card constructed but never shown (what an overlay.Show() that threw
    /// leaves behind) got the flag anyway, and the next capture's revive loop faded a card
    /// onto the screen that nothing had ever asked to show.
    /// </summary>
    [Fact]
    public void HidingTheStackDoesNotFlagACardThatWasNeverShown()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            var shown = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            var never = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            try
            {
                shown.Show();
                PumpUntil(() => Motion.Holders == 0, 4000);

                Invoke(shown, "HideEveryCard");
                PumpUntil(() => !shown.Visible, 3000);

                Assert.True((bool)Field(shown, "_temporarilyHidden")!);
                Assert.False(
                    (bool)Field(never, "_temporarilyHidden")!,
                    "a card that was never shown was flagged as temporarily hidden");
            }
            finally
            {
                DisposeAll(new List<FastQuickActionsWindow> { shown, never });
                PumpUntil(() => Motion.Holders == 0, 3000);
            }
        });
    }

    /// <summary>
    /// ComeBack re-renders the thumbnail for the editor round-trip, and that render drops
    /// the blurred pair the hover cross-fade paints. Only the async pass makes another one,
    /// and its queue guard used to be once-per-card — so a card that came back without
    /// going through OnImageChanged (Done with nothing drawn: the editor returns before
    /// ImageCommitted fires) kept a null blur and a bilinear thumbnail for good, and hover
    /// painted the sharp picture under a flat tint with no frost at all.
    /// </summary>
    [Fact]
    public void ACardComingBackFromTheEditorGetsItsFrostedHoverBack()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
            try
            {
                card.Show();
                PumpUntil(() => Field(card, "_blurredPreview") is not null, 6000);
                Assert.NotNull(Field(card, "_blurredPreview"));

                // The annotate round-trip: step aside for the editor, come back with the
                // image untouched.
                Invoke(card, "StepAside");
                PumpUntil(() => !card.Visible, 3000);
                card.ComeBack();

                PumpUntil(() => Field(card, "_blurredPreview") is not null, 6000);
                Assert.NotNull(Field(card, "_blurredPreview"));
            }
            finally
            {
                DisposeAll(new List<FastQuickActionsWindow> { card });
                PumpUntil(() => Motion.Holders == 0, 3000);
            }
        });
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
