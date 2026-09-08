using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Ocr;
using WinShot.Pin;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The fifth review round's probes, kept as tests. The three that failed on the branch before
/// the fix beside them: a scroll-chrome exit that started at full opacity no matter where the
/// entrance had got to (so cancelling inside the ramp made the whole-monitor dim brighten
/// before it left), a toast replaced by its own result being disposed instead of closed (so the
/// "Reading text" -> result handoff blinked), and the toast's two new controls -- the line-break
/// toggle and "Don't detect links" -- reachable by pointer only.
/// </summary>
public class BreakerProbesRound5
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private const BindingFlags NonPub = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Priv(object target, string name) =>
        target.GetType().GetField(name, NonPub)!.GetValue(target);

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    // ---- A. cancelling a chrome fade-in and immediately fading out ----------

    /// <summary>
    /// Out() drove 1 - p from p = 0, so its first frame wrote 255 wherever the surface actually
    /// was. It takes the live alpha now, and has no default for it: an exit that has to be told
    /// where it starts is one the compiler makes every caller answer.
    /// </summary>
    [Fact]
    public void A_ChromeFadeOut_FromAPartialFadeIn_DoesNotJumpBackToFullAlpha()
    {
        using var _ = new Anim(true);
        byte alpha = 0;
        IDisposable inHandle = ScrollChromeFade.In(a => alpha = a);
        PumpUntil(() => alpha > 30 && alpha < 200, 2000);
        byte atCancel = alpha;
        Assert.InRange(atCancel, (byte)1, (byte)230);

        inHandle.Dispose();
        var seen = new List<byte>();
        Task done = ScrollChromeFade.Out(a => { seen.Add(a); alpha = a; }, atCancel, out IDisposable outHandle);
        PumpUntil(() => done.IsCompleted, 3000);
        outHandle.Dispose();

        // The first frame of the exit is where a pop would show, and the last is gone.
        Assert.True(seen.Count > 0);
        Assert.True(seen[0] <= atCancel,
            $"exit started at {seen[0]} after the entrance was cancelled at {atCancel} -- the chrome brightens before it leaves");
        Assert.Equal(0, seen[^1]);
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    /// <summary>
    /// And the surfaces themselves pass their live value. Escape inside the 280 ms entrance is
    /// the case: the bar must never be brighter during its exit than it was when the exit began.
    /// </summary>
    [Fact]
    public void A_ControlsBar_ExitNeverBrightensPastWhereTheEntranceGotTo()
    {
        using var _ = new Anim(true);
        using var bar = new ScrollControlsBar(new SD.Rectangle(200, 200, 900, 600));
        bar.StartPosition = WF.FormStartPosition.Manual;
        bar.Location = new SD.Point(-20_000, -20_000);
        bar.Show();
        bar.Location = new SD.Point(-20_000, -20_000);

        byte Alpha() => (byte)Priv(bar, "_alpha")!;
        PumpUntil(() => Alpha() is > 30 and < 200, 2000);
        byte atCancel = Alpha();
        Assert.InRange(atCancel, (byte)1, (byte)230);

        Task done = bar.FadeOutAsync();
        byte peak = 0;
        var d = Stopwatch.StartNew();
        while (!done.IsCompleted && d.ElapsedMilliseconds < 3000)
        {
            peak = Math.Max(peak, Alpha());
            WF.Application.DoEvents();
            Thread.Sleep(1);
        }

        Assert.True(peak <= atCancel,
            $"the bar reached {peak} during an exit that began at {atCancel}");
        Assert.Equal(0, Alpha());
        bar.Hide();
        PumpUntil(() => Motion.Holders == 0, 2000);
        Assert.Equal(0, Motion.Holders);
    }

    // ---- B. one-entry pill measurement cache, two live strings --------------

    [Fact]
    public void B_SelectorPill_AlternatingStringsStillMeasureCorrectly()
    {
        const string a = "1920 x 1080";
        const string b = "Drag over the part that scrolls, then scroll up";

        SD.Size firstA = SelectorChrome.MeasureLabel(a);
        SD.Size firstB = SelectorChrome.MeasureLabel(b);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(firstA, SelectorChrome.MeasureLabel(a));
            Assert.Equal(firstB, SelectorChrome.MeasureLabel(b));
        }
        Assert.True(firstB.Width > firstA.Width);
    }

    // ---- C. colour maths corners ------------------------------------------

    [Theory]
    [InlineData(0, 0, 255, 240)]     // pure blue
    [InlineData(255, 0, 255, 300)]   // magenta: max == r AND max == b, g < b -> +6 wrap branch
    [InlineData(0, 255, 255, 180)]   // cyan
    [InlineData(255, 0, 0, 0)]       // red
    public void C_Hsl_HueIsCorrectAtTheWrapPoints(int r, int g, int b, int expectedHue)
    {
        string hsl = ColorFormatter.Format(SD.Color.FromArgb(r, g, b), ColorFormat.Hsl);
        Assert.StartsWith($"hsl({expectedHue},", hsl);
    }

    [Fact]
    public void C_Oklch_BlackAndWhiteHaveNoNaN()
    {
        foreach (SD.Color c in new[] { SD.Color.Black, SD.Color.White })
        {
            string s = ColorFormatter.Format(c, ColorFormat.Oklch);
            Assert.DoesNotContain("NaN", s);
            Assert.DoesNotContain("∞", s);
        }
    }

    // ---- D. OCR language plumbing -----------------------------------------

    [Fact]
    public void D_PreferredLanguage_TolerantOfNullAndBlank()
    {
        string original = OcrService.PreferredLanguage;
        try
        {
            OcrService.PreferredLanguage = null!;
            Assert.Equal(OcrService.AutoLanguage, OcrService.PreferredLanguage);
            OcrService.PreferredLanguage = "   ";
            Assert.Equal(OcrService.AutoLanguage, OcrService.PreferredLanguage);
            OcrService.PreferredLanguage = "  en-US ";
            Assert.Equal("en-US", OcrService.PreferredLanguage);
        }
        finally
        {
            OcrService.PreferredLanguage = original;
        }
    }

    [Fact]
    public void D_AvailableLanguages_NeverThrowsAndNeverReturnsNull()
    {
        var langs = OcrService.AvailableLanguages();
        Assert.NotNull(langs);
        Assert.All(langs, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Tag));
            Assert.False(string.IsNullOrWhiteSpace(l.Display));
        });
    }

    // ---- E. an old settings.json with explicit nulls in the new fields ------

    [Fact]
    public void E_SettingsWithExplicitNulls_DoNotBreakTheNewConsumers()
    {
        const string json = """
            {
              "HotkeyCaptureRegion": "Ctrl+Shift+1",
              "OcrLanguage": null,
              "HotkeyPinLast": null,
              "HotkeyPinCloseAll": null,
              "HotkeyPinToggleVisibility": null,
              "SavedColors": null
            }
            """;
        var s = JsonSerializer.Deserialize<Settings>(json)!;

        // SavedColors / OcrLink already have null guards; these are the new ones.
        Assert.Empty(SavedColors.Sanitize(s.SavedColors));
        Assert.Single(SavedColors.With(s.SavedColors, SD.Color.Red));

        // The hotkey registrar's own gate.
        foreach (string? gesture in new[] { s.HotkeyPinLast, s.HotkeyPinCloseAll, s.HotkeyPinToggleVisibility })
            Assert.True(string.IsNullOrWhiteSpace(gesture));

        // OcrService normalises a null language rather than caching one.
        string original = OcrService.PreferredLanguage;
        try
        {
            OcrService.PreferredLanguage = s.OcrLanguage;
            Assert.Equal(OcrService.AutoLanguage, OcrService.PreferredLanguage);
        }
        finally { OcrService.PreferredLanguage = original; }

        // And a round-trip through the writer must not throw on the nulls.
        string written = JsonSerializer.Serialize(s);
        Assert.NotNull(JsonSerializer.Deserialize<Settings>(written));
    }

    // ---- F. pins: toggle then close, mid-ramp -----------------------------

    [Fact]
    public void F_CloseAllInsideAVisibilityRamp_LeavesNoHolderAndNoLivePins()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var pins = new List<FastPinWindow>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var pin = new FastPinWindow(new SD.Bitmap(120, 90));
                pins.Add(pin);
                pin.Show();
            }
            PumpFor(30);
            Assert.True(FastPinWindow.AnyOpen);

            FastPinWindow.ToggleAllVisibility();      // starts three 140 ms ramps
            PumpFor(30);                              // land inside them
            FastPinWindow.CloseAll();                 // close mid-ramp

            PumpUntil(() => Motion.Holders == 0, 3000);
            Assert.Equal(0, Motion.Holders);
            Assert.False(FastPinWindow.AnyOpen);
        }
        finally
        {
            foreach (var p in pins) { try { p.Dispose(); } catch { } }
        }
    }

    [Fact]
    public void G_HidingAndShowingKeepsAUserChosenOpacity()
    {
        using var _ = new Anim(true);
        var pin = new FastPinWindow(new SD.Bitmap(120, 90));
        try
        {
            pin.Show();
            PumpFor(20);
            pin.Opacity = 0.6;

            FastPinWindow.ToggleAllVisibility();       // hide
            PumpUntil(() => !pin.Visible, 3000);
            FastPinWindow.ToggleAllVisibility();       // show
            PumpUntil(() => pin.Visible && pin.Opacity > 0.55, 3000);
            PumpFor(250);

            Assert.True(pin.Visible);
            Assert.InRange(pin.Opacity, 0.55, 0.65);
        }
        finally
        {
            try { pin.Close(); pin.Dispose(); } catch { }
            PumpUntil(() => Motion.Holders == 0, 2000);
        }
    }

    // ---- H. a pin whose scale is set by the wheel keeps the chip honest ----

    [Fact]
    public void H_ZoomChipTextIsMonotonic()
    {
        Assert.Equal("100%", FastPinWindow.ZoomChipText(1.0));
        Assert.Equal("50%", FastPinWindow.ZoomChipText(0.5));
        Assert.Equal("10%", FastPinWindow.ZoomChipText(0.1));
        Assert.Equal("5%", FastPinWindow.ZoomChipText(0.05));
        Assert.Equal("0.4%", FastPinWindow.ZoomChipText(0.004));
        Assert.Equal("0.1%", FastPinWindow.ZoomChipText(0.0));
    }

    // ---- I. reverse scrolling: the command surface agrees with the loop ----

    [Fact]
    public void I_ScrollUpCommand_PresetsVerticalAndReverse()
    {
        Assert.Equal(ScrollDirection.Vertical, ScrollCaptureCommand.DirectionForCommand("scroll-up"));
        Assert.True(ScrollCaptureCommand.ReverseForCommand("scroll-up"));
        Assert.Equal(ScrollDirection.Horizontal, ScrollCaptureCommand.DirectionForCommand("scroll-horizontal"));
        Assert.False(ScrollCaptureCommand.ReverseForCommand("scroll-horizontal"));
        Assert.Null(ScrollCaptureCommand.DirectionForCommand("scrolling"));
        Assert.False(ScrollCaptureCommand.ReverseForCommand("scrolling"));
    }

    // ---- J. the auto-scroll gate is the only wheel-injection path ----------

    [Fact]
    public void J_GateBlocksTheWheelWhenThePointerIsOffTheRegionOnANegativeYMonitor()
    {
        var region = new SD.Rectangle(-1920, -1080, 800, 600);
        Assert.True(ScrollAutoScrollGate.MayInject(true, region, new SD.Point(-1500, -800)));
        Assert.False(ScrollAutoScrollGate.MayInject(true, region, new SD.Point(100, 100)));
        // A posted wheel message / page key is not cursor-routed, so it is never gated.
        Assert.True(ScrollAutoScrollGate.MayInject(false, region, new SD.Point(100, 100)));
    }

    // ---- K. the working toast hands over to its result instead of blinking --

    /// <summary>
    /// OnShown used to Dispose() the toast it replaced. Dispose never raises OnFormClosing,
    /// which is where the exit lives, so "Reading text" vanished at full opacity and the corner
    /// stood empty for the 280 ms the result spent rising. It is closed now: the outgoing card
    /// runs its exit under the arriving one, and still ends up disposed with no clock holder.
    /// </summary>
    [Fact]
    public void K_AToastReplacedByItsResultRunsItsExitInsteadOfBeingCut()
    {
        using var _ = new Anim(true);
        var working = new ToastWindow("Reading text", "Working on the selection.",
            new SD.Point(300, 300), null, ToastSeverity.Info, false, new ToastExtras { Sticky = true });
        var result = new ToastWindow("Text copied", "hello", new SD.Point(300, 300), null);
        try
        {
            working.Show();
            PumpFor(60);
            result.Show();
            PumpFor(40);

            Assert.False(working.IsDisposed);
            Assert.True((bool)Priv(working, "_exiting")!,
                "the replaced toast was torn down instead of being allowed to leave");

            PumpUntil(() => working.IsDisposed, 3000);
            Assert.True(working.IsDisposed);
        }
        finally
        {
            try { working.Dispose(); } catch { }
            try { result.Dispose(); } catch { }
            PumpUntil(() => Motion.Holders == 0, 2000);
        }
        Assert.Equal(0, Motion.Holders);
    }

    // ---- L. the toast's two new controls answer to the keyboard ------------

    /// <summary>
    /// The line-break toggle rides an ordinary 4.5 s toast with no primary and no secondary,
    /// so the hook -- gated on "sticky AND has a button" -- was never armed for it and the
    /// button was mouse-only. Enter and Space both take it now, on the same hover gate the
    /// other keys use.
    /// </summary>
    [Fact]
    public void L_TheLineBreakToggleIsReachableWithoutAPointer()
    {
        using var _ = new Anim(false);
        using var toast = new ToastWindow("Text copied", "two lines", null, null,
            ToastSeverity.Success, false,
            new ToastExtras { ToggleText = "Cut breaks", Toggle = () => Task.FromResult("Add breaks") });
        toast.Show();
        PumpFor(40);

        Assert.NotEqual(IntPtr.Zero, (IntPtr)Priv(toast, "_keyHook")!);

        toast.Location = WF.Cursor.Position;
        PumpFor(20);
        Assert.True(toast.ConsumesKey(WF.Keys.Enter));
        Assert.True(toast.ConsumesKey(WF.Keys.Space));

        var button = Assert.Single(toast.Controls.OfType<DarkButton>());
        typeof(ToastWindow).GetMethod("PressWithSpace", NonPub)!.Invoke(toast, new object[] { button });
        PumpUntil(() => button.Text == "Add breaks", 2000);
        Assert.Equal("Add breaks", button.Text);
    }

    /// <summary>
    /// The "Don't detect links" checkbox is the one control that exists to stop a recurring
    /// offer, and it rode a toast whose hook mapped Enter and Escape only -- so the standing
    /// answer was pointer-only. Space ticks it, the way Space ticks a checkbox anywhere else.
    /// </summary>
    [Fact]
    public void L_TheDontDetectLinksCheckboxIsReachableWithoutAPointer()
    {
        using var _ = new Anim(false);
        bool? suppressed = null;
        using var toast = new ToastWindow("Open this link?", "https://example.com",
            new SD.Point(400, 400), () => { }, ToastSeverity.Info, false,
            new ToastExtras
            {
                Sticky = true,
                SecondaryText = "Don't open",
                CheckboxText = "Don't detect links",
                CheckboxChanged = v => suppressed = v,
            });
        toast.Show();
        PumpFor(40);

        toast.Location = WF.Cursor.Position;
        PumpFor(20);
        Assert.True(toast.ConsumesKey(WF.Keys.Space));

        var check = Assert.Single(toast.Controls.OfType<DarkCheckBox>());
        typeof(ToastWindow).GetMethod("PressWithSpace", NonPub)!.Invoke(toast, new object[] { check });
        Assert.True(check.Checked);
        Assert.True(suppressed);
    }
}
