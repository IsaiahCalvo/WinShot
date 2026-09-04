using System.Threading;
using WinShot.Capture;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The all-in-one selector's restored region used to be a rectangle you could only accept or
/// redraw — a press anywhere threw it away. These drive the same handlers the surfaces call
/// (no real mouse, no visible window) to prove it now moves and resizes.
/// </summary>
[Collection("MotionRefcount")]
public class AllInOneSelectorInteractionTests
{
    private sealed class Pointer : IDisposable
    {
        public SD.Point At;

        public Pointer(int x, int y)
        {
            At = new SD.Point(x, y);
            FastAllInOneSelectorDialog.CursorProvider = () => At;
        }

        public void MoveTo(FastAllInOneSelectorDialog selector, int x, int y)
        {
            At = new SD.Point(x, y);
            selector.HandleMouseMove();
        }

        public void Dispose() => FastAllInOneSelectorDialog.CursorProvider = null;
    }

    private static WF.MouseEventArgs Left() => new(WF.MouseButtons.Left, 1, 0, 0, 0);

    /// <summary>Seeds the pending region the way TryRestoreLastRegion does, in virtual px.</summary>
    private static void SetPending(FastAllInOneSelectorDialog selector, SD.Rectangle virtualRect) =>
        typeof(FastAllInOneSelectorDialog)
            .GetField("_pendingPx", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(selector, virtualRect);

    private static SD.Rectangle VirtualScreen => CaptureService.VirtualScreen;

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

    [Fact]
    public void ARestoredRegionCanBeResizedByItsCornerHandle()
    {
        RunSta(() =>
        {
            SD.Rectangle vs = VirtualScreen;
            var region = new SD.Rectangle(100, 100, 300, 200); // virtual px
            SD.Point bottomRight = new(vs.X + 400, vs.Y + 300);

            using var pointer = new Pointer(bottomRight.X, bottomRight.Y);
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetPending(selector, region);

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, bottomRight.X + 120, bottomRight.Y + 60);
            selector.HandleMouseUp(Left());

            Assert.Equal(new SD.Rectangle(100, 100, 420, 260), selector.PendingRegion);
        });
    }

    [Fact]
    public void PressingInsideARestoredRegionMovesItInsteadOfThrowingItAway()
    {
        RunSta(() =>
        {
            SD.Rectangle vs = VirtualScreen;
            var region = new SD.Rectangle(100, 100, 300, 200);
            SD.Point inside = new(vs.X + 250, vs.Y + 200);

            using var pointer = new Pointer(inside.X, inside.Y);
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetPending(selector, region);

            selector.HandleMouseDown(Left());
            pointer.MoveTo(selector, inside.X + 50, inside.Y + 25);
            selector.HandleMouseUp(Left());

            SD.Rectangle? moved = selector.PendingRegion;
            Assert.NotNull(moved);
            Assert.Equal(new SD.Size(300, 200), moved!.Value.Size);
            Assert.Equal(new SD.Point(150, 125), moved.Value.Location);
        });
    }

    [Fact]
    public void PressingOutsideARestoredRegionStillStartsAFreshMarquee()
    {
        RunSta(() =>
        {
            SD.Rectangle vs = VirtualScreen;
            using var pointer = new Pointer(vs.X + 800, vs.Y + 700);
            using var selector = new FastAllInOneSelectorDialog(
                () => Task.FromResult(new List<WindowInfo>()), settings: null);
            SetPending(selector, new SD.Rectangle(100, 100, 300, 200));

            selector.HandleMouseDown(Left());
            Assert.True(selector.IsDragging);
            Assert.True(selector.CapturingPointer);

            selector.HandleKeyDown(new WF.KeyEventArgs(WF.Keys.Escape));
        });
    }
}
