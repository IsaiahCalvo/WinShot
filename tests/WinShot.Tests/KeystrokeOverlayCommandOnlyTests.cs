using System.Reflection;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// "Show only command keys" keeps plain typing out of the pill's TEXT. It used to refresh the
/// pill's visibility clock anyway, so every plain keystroke re-pinned — and could resurrect —
/// the chord before it: press Ctrl+S, type a paragraph, and "Ctrl S" stayed burned into the
/// recording for the whole passage. The chord's dwell and the typing state are two clocks now,
/// and these pin them apart.
/// </summary>
[Collection("MotionRefcount")]
public class KeystrokeOverlayCommandOnlyTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static readonly SD.Rectangle Off = new(-6000, -6000, 600, 400);

    private static FastKeystrokeOverlayWindow CommandOnlyOverlay()
    {
        ConstructorInfo ctor = typeof(FastKeystrokeOverlayWindow).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(SD.Rectangle), typeof(KeystrokeOverlayOptions), typeof(bool)], null)!;
        return (FastKeystrokeOverlayWindow)ctor.Invoke(
            [Off, new KeystrokeOverlayOptions(Render: true, CommandOnly: true, Position: "bottom-center"), false]);
    }

    private static void Invoke(object t, string n, params object?[] a) =>
        t.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(t, a);

    private static T Field<T>(object t, string n) =>
        (T)t.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(t)!;

    private static void SetField(object t, string n, object? v) =>
        t.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(t, v);

    [Fact]
    public void FilteredTypingDoesNotBringAFadedChordBack()
    {
        using var _ = new Anim(true);
        using FastKeystrokeOverlayWindow overlay = CommandOnlyOverlay();

        Invoke(overlay, "Display", "Ctrl S", false, true);      // chord -> pill shows it
        SetField(overlay, "_shownAtMs", Environment.TickCount64 - 5000L);
        Invoke(overlay, "AdvanceFade");                          // 1.5 s + 350 ms elapsed
        Assert.Equal(0d, Field<double>(overlay, "_opacity"));

        Invoke(overlay, "Display", "h", true, false);            // plain typing, filtered out
        Invoke(overlay, "Display", "i", true, false);
        Invoke(overlay, "AdvanceFade");

        Assert.Equal("Ctrl S", Field<string>(overlay, "_run"));
        Assert.Equal(0d, Field<double>(overlay, "_opacity"));
    }

    [Fact]
    public void FilteredTypingDoesNotExtendTheChordsVisibility()
    {
        using var _ = new Anim(true);
        using FastKeystrokeOverlayWindow overlay = CommandOnlyOverlay();

        Invoke(overlay, "Display", "Ctrl S", false, true);
        long shownAt = Field<long>(overlay, "_shownAtMs");
        Thread.Sleep(5);
        Invoke(overlay, "Display", "h", true, false);

        Assert.Equal(shownAt, Field<long>(overlay, "_shownAtMs"));
    }

    /// <summary>The other half of the split: filtered typing still has to keep the typing state
    /// alive, because "hide the cursor while typing" is the only thing that reads it.</summary>
    [Fact]
    public void FilteredTypingStillKeepsTheTypingStateAlive()
    {
        using var _ = new Anim(true);
        using FastKeystrokeOverlayWindow overlay = CommandOnlyOverlay();

        var states = new List<bool>();
        overlay.TypingChanged += states.Add;

        Invoke(overlay, "Display", "Ctrl S", false, true);
        Assert.Equal([true], states);

        // The chord's own clock runs out; the typist has not stopped.
        SetField(overlay, "_shownAtMs", Environment.TickCount64 - 5000L);
        Invoke(overlay, "Display", "h", true, false);
        Invoke(overlay, "AdvanceFade");

        Assert.Equal(0d, Field<double>(overlay, "_opacity"));
        Assert.True(Field<bool>(overlay, "_typing"));

        // Only silence ends it.
        SetField(overlay, "_typingAtMs", Environment.TickCount64 - 5000L);
        Invoke(overlay, "AdvanceFade");
        Assert.False(Field<bool>(overlay, "_typing"));
        Assert.Equal([true, false], states);
    }

    /// <summary>Form.Dispose() does not raise Closed. Before the teardown moved into
    /// Dispose(bool), disposing an overlay that had ever displayed a key left the process-wide
    /// high-resolution clock raised forever.</summary>
    [Fact]
    public void DisposeWithoutCloseReleasesTheMotionClock()
    {
        using var _ = new Anim(true);
        int before = Motion.Holders;

        FastKeystrokeOverlayWindow overlay = CommandOnlyOverlay();
        Invoke(overlay, "Display", "Ctrl S", false, true);
        Assert.True(Motion.Holders > before);

        overlay.Dispose();
        WF.Application.DoEvents();

        Assert.Equal(before, Motion.Holders);
    }
}
