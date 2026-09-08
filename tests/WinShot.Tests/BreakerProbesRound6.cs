using System.Diagnostics;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The sixth review round's probes, kept as tests. The two that failed on the branch before the
/// fix beside them are both the tall-capture flyout outliving what it was placed against: it was
/// cut down in one frame whenever the card's pixels were edited, and it was never told when the
/// card itself moved -- so it hung at full opacity while the card flew off screen, and covered
/// the card it belongs to after a restack. The rest are the round's probes that passed and are
/// worth keeping: chip reachability on odd-shaped pins, a locked pin's way back, the toast's link
/// vocabulary fitting its own card, and a reverse capture's previews reading the right way up.
/// </summary>
public class BreakerProbesRound6
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private const BindingFlags NonPub = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Call(object t, string name, params object?[] args) =>
        t.GetType().GetMethod(name, NonPub)!.Invoke(t, args);

    private static object? Prop(object t, string name) =>
        t.GetType().GetProperty(name, NonPub)!.GetValue(t);

    private static object? Fld(object t, string name) =>
        t.GetType().GetField(name, NonPub)!.GetValue(t);

    private static void SetFld(object t, string name, object? v) =>
        t.GetType().GetField(name, NonPub)!.SetValue(t, v);

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static SD.Bitmap Solid(int w, int h, SD.Color c)
    {
        var b = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(c);
        return b;
    }

    /// <summary>Brings a card up over a strip tall enough to earn a flyout and waits for the
    /// off-thread resample to raise one.</summary>
    private static WF.Form RaiseFlyout(FastQuickActionsWindow card)
    {
        card.Show();
        PumpFor(240);
        Call(card, "SetHovering", true);
        PumpUntil(() => Fld(card, "_tallPreview") is not null);
        object? preview = Fld(card, "_tallPreview");
        Assert.True(preview is not null, "the tall-capture flyout never came up; the probe proves nothing");
        return (WF.Form)preview!;
    }

    // ---- A. the flyout leaves when the pixels under it change ---------------

    /// <summary>
    /// A rotate/flip/resize from the card used to call CloseTallCapturePreviewNow, which skipped
    /// the exit tween because the card was about to dispose the bitmap the flyout was painting
    /// from. The window is handed that bitmap now, so it sinks out under the replacement instead
    /// of blinking away in a single frame.
    /// </summary>
    [Fact]
    public void A_EditingTheCardsImage_RetiresTheFlyoutWithItsExitTween()
    {
        RunSta(() =>
        {
            using var _ = new Anim(true);
            var tall = new SD.Bitmap(700, 6000, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.SteelBlue);

            using var card = new FastQuickActionsWindow(tall, new SettingsService());
            WF.Form flyout = RaiseFlyout(card);

            Call(card, "OnImageChanged");

            // Synchronous, no pumping: the old window must still be on screen the instant the
            // edit returns. It used to be Close()d and Dispose()d inside this call.
            Assert.False(flyout.IsDisposed,
                "the flyout was torn down inside the edit, so its 140 ms exit fade never ran");
            Assert.Null(Fld(card, "_tallPreview"));
            Assert.Null(Fld(card, "_tallScaled"));

            PumpUntil(() => flyout.IsDisposed, 3000);
            Assert.True(flyout.IsDisposed, "the retired flyout never finished its exit");
            card.Close();
            PumpFor(150);
        });
    }

    // ---- B. the flyout leaves when the card flies out -----------------------

    /// <summary>
    /// The flyout is placed from the card's bounds once and is an owned window, so it follows the
    /// card's show/hide but not its position. BeginExitMotion used to leave it hanging at full
    /// opacity for the whole 360 ms fly-out and then blink out with the handle.
    /// </summary>
    [Fact]
    public void B_TheCardFlyingOut_TakesItsFlyoutWithIt()
    {
        RunSta(() =>
        {
            using var _ = new Anim(true);
            var tall = new SD.Bitmap(700, 6000, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.SteelBlue);

            var card = new FastQuickActionsWindow(tall, new SettingsService());
            WF.Form flyout = RaiseFlyout(card);
            SD.Rectangle cardBefore = card.Bounds;

            Call(card, "BeginExitMotion");
            Assert.False(flyout.IsDisposed, "the flyout was cut rather than faded");
            Assert.Null(Fld(card, "_tallPreview"));

            // 140 ms of fade inside a 360 ms fly-out: the flyout is gone before the card is.
            PumpUntil(() => flyout.IsDisposed, 3000);
            Assert.True(flyout.IsDisposed,
                $"card flew from {cardBefore} to {card.Bounds} while its flyout stayed at {flyout.Bounds}");

            PumpUntil(() => card.IsDisposed, 3000);
            if (!card.IsDisposed) card.Dispose();
        });
    }

    // ---- C. the flyout leaves when a second capture restacks the card -------

    /// <summary>
    /// A restack slides the card by its own height + 12 while the flyout stays where it was
    /// placed, so the flyout ended up detached from -- and overlapping -- the card it belongs
    /// to. The card retires it when it starts moving and raises a correctly placed one once it
    /// has landed.
    /// </summary>
    [Fact]
    public void C_ARestackDoesNotLeaveTheFlyoutOverTheCard()
    {
        RunSta(() =>
        {
            using var _ = new Anim(true);
            var tall = new SD.Bitmap(700, 6000, SD.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = SD.Graphics.FromImage(tall)) g.Clear(SD.Color.SteelBlue);

            var settings = new SettingsService();
            // The card under it is what makes the tall card restack: a new card stacks above
            // the open ones, and the ones above a card that closes slide down into its place.
            var below = new FastQuickActionsWindow(Solid(400, 300, SD.Color.Coral), settings);
            below.Show();
            PumpFor(300);

            using var card = new FastQuickActionsWindow(tall, settings);
            WF.Form flyout = RaiseFlyout(card);
            SD.Rectangle cardBefore = card.Bounds;

            below.Close();
            PumpFor(1400);

            SD.Rectangle cardAfter = card.Bounds;
            Assert.True(cardAfter != cardBefore, "the card never restacked; the probe proves nothing");
            Assert.True(flyout.IsDisposed,
                $"card restacked {cardBefore} -> {cardAfter}; its flyout stayed at {flyout.Bounds}");

            // Whatever is up now was placed against the card's new bounds, so it sits clear of it.
            if (Fld(card, "_tallPreview") is WF.Form raised && !raised.IsDisposed && raised.Visible)
                Assert.False(raised.Bounds.IntersectsWith(cardAfter),
                    $"the re-raised flyout at {raised.Bounds} covers the card at {cardAfter}");

            card.Close();
            PumpFor(200);
        });
    }

    // ---- D. every chip on an odd-shaped pin is reachable --------------------

    /// <summary>Replays HitTestResizeBorder for an arbitrary client point (the real one reads
    /// Cursor.Position, which tests must not move).</summary>
    private static int HitAt(FastPinWindow pin, SD.Point p)
    {
        int w = pin.ClientSize.Width, h = pin.ClientSize.Height;
        int border = (int)Call(pin, "Ui", 6)!;
        bool left = p.X <= border, right = p.X >= w - border;
        bool top = p.Y <= border, bottom = p.Y >= h - border;
        if ((top || bottom || left || right) && (int)Call(pin, "HitTestButton", p)! >= 0) return 1;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 1;
    }

    [Theory]
    [InlineData(200, 30)]
    [InlineData(20, 200)]
    [InlineData(40, 40)]
    [InlineData(168, 40)]
    public void D_ChipPixelsAreNeverAResizeBorder(int w, int h)
    {
        using var _ = new Anim(false);
        using var pin = new FastPinWindow(Solid(w, h, SD.Color.Teal));
        pin.Show();
        PumpFor(30);
        SetFld(pin, "_mouseInside", true);

        int[] visible = (int[])Prop(pin, "VisibleButtons")!;
        Assert.NotEmpty(visible);
        int last = visible[^1]; // Close

        var bounds = (SD.Rectangle)Call(pin, "ButtonBounds", last)!;
        Assert.False(bounds.IsEmpty);

        int stolen = 0, total = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                total++;
                if (HitAt(pin, new SD.Point(x, y)) != 1) stolen++;
            }

        pin.Close();
        Assert.True(stolen == 0,
            $"pin {w}x{h}: {stolen}/{total} pixels of the Close chip ({bounds}) hit-test as a resize border");
    }

    // ---- E. a pin locked from the keyboard still has a way back -------------

    [Fact]
    public void E_LockingWithNoPointerLeavesAWayBack()
    {
        using var _ = new Anim(false);
        using var pin = new FastPinWindow(Solid(400, 300, SD.Color.Olive));
        pin.Show();
        PumpFor(30);

        Call(pin, "SetLocked", true);
        PumpFor(30);
        Assert.True((bool)Fld(pin, "_locked")!);

        Call(pin, "NoteLockedHover");
        PumpFor(30);
        int[] visible = (int[])Prop(pin, "VisibleButtons")!;
        Assert.Equal(2, visible.Length);
        foreach (int i in visible)
        {
            var b = (SD.Rectangle)Call(pin, "ButtonBounds", i)!;
            Assert.False(b.IsEmpty);
            Assert.Equal(i, (int)Call(pin, "HitTestButton",
                new SD.Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2)))!);
        }
        pin.Close();
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- F. the toast's link vocabulary fits its own card -------------------

    [Fact]
    public void F_LinkToast_KeepsEveryControlInsideItsCard()
    {
        using var _ = new Anim(false);
        var extras = new ToastExtras
        {
            Sticky = true,
            SecondaryText = "Don't open",
            CheckboxText = "Don't detect links",
        };
        using var toast = new ToastWindow(
            "QR code copied",
            "https://example.com/a/rather/long/path/that/wraps/over/two/lines",
            null, () => { }, ToastSeverity.Success, false, extras);
        toast.Show();
        PumpFor(40);

        foreach (WF.Control c in toast.Controls)
        {
            Assert.True(c.Right <= toast.ClientSize.Width,
                $"{c.GetType().Name} '{c.Text}' right {c.Right} > client {toast.ClientSize.Width}");
            Assert.True(c.Bottom <= toast.ClientSize.Height,
                $"{c.GetType().Name} '{c.Text}' bottom {c.Bottom} > client {toast.ClientSize.Height}");
            Assert.True(c.Left >= 0 && c.Top >= 0, $"{c.GetType().Name} at {c.Location}");
        }

        var buttons = toast.Controls.OfType<DarkButton>().ToList();
        for (int i = 0; i < buttons.Count; i++)
            for (int j = i + 1; j < buttons.Count; j++)
                Assert.False(buttons[i].Bounds.IntersectsWith(buttons[j].Bounds),
                    $"{buttons[i].Text} overlaps {buttons[j].Text}");

        toast.Close();
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- G. a reverse capture's live previews read in document order --------

    /// <summary>A reverse capture runs the whole matcher in flipped space and unflips on the way
    /// out; a preview snapshot that missed the unflip is upside down on screen.</summary>
    [Fact]
    public void G_ReverseCapture_PreviewFramesAreInDocumentOrder()
    {
        var frames = new List<SD.Bitmap>();
        try
        {
            using var cts = new CancellationTokenSource();
            var positions = Enumerable.Range(0, 16).Select(i => 600 - (i * 40)).ToList();
            int index = 0;

            SD.Bitmap FrameSource(SD.Rectangle _)
            {
                if (index >= positions.Count) { cts.Cancel(); return DocFrame(positions[^1]); }
                return DocFrame(positions[index++]);
            }

            using SD.Bitmap? stitched = ScrollingCaptureService.RunAsync(
                new SD.Rectangle(0, 0, 200, 100),
                ScrollDirection.Vertical,
                autoScroll: () => false,
                status: _ => { },
                hint: _ => { },
                cts.Token,
                preview: b => frames.Add(b),
                frameSource: FrameSource,
                paneScoped: false,
                cursorSource: null,
                scrollInput: null,
                reverse: true).GetAwaiter().GetResult();

            Assert.NotNull(stitched);
            Assert.NotEmpty(frames);
            foreach (SD.Bitmap f in frames)
            {
                if (f.Height < 4) continue;
                int top = DocRow(f.GetPixel(5, 0));
                int bottom = DocRow(f.GetPixel(5, f.Height - 1));
                Assert.True(top >= 0 && bottom >= 0, "preview row colour was not a document row");
                Assert.True(top < bottom,
                    $"preview {f.Width}x{f.Height} runs bottom-to-top: top row is doc {top}, bottom row doc {bottom}");
            }
            Assert.True(DocRow(stitched!.GetPixel(5, 0)) < DocRow(stitched.GetPixel(5, stitched.Height - 1)));
        }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    /// <summary>Recovers the document row a colour from <see cref="DocFrame"/> encodes, or -1.</summary>
    private static int DocRow(SD.Color c) => c.B == 0x40 ? (c.R << 8) | c.G : -1;

    /// <summary>A 200x100 window onto a tall document whose row colour encodes its y.</summary>
    private static SD.Bitmap DocFrame(int top)
    {
        var b = new SD.Bitmap(200, 100);
        for (int y = 0; y < 100; y++)
        {
            int doc = top + y;
            SD.Color c = SD.Color.FromArgb(255, doc >> 8, doc & 0xFF, 0x40);
            for (int x = 0; x < 200; x++) b.SetPixel(x, y, c);
        }
        return b;
    }
}
