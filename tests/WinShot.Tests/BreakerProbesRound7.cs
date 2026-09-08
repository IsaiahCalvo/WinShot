using System.Diagnostics;
using System.IO;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Whole-app integration guards from the round-7 breaker pass: the corner surfaces that
/// arbitrate against each other, the default hotkey set, big and empty history folders, the
/// card's keyboard hook lifetime, and the clipboard's real exception type.
/// </summary>
public class BreakerProbesRound7
{
    private const BindingFlags NonPub = BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Fld(object t, string name) =>
        t.GetType().GetField(name, NonPub)!.GetValue(t);

    private static object? Call(object t, string name, params object?[] args) =>
        t.GetType().GetMethod(name, NonPub)!.Invoke(t, args);

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception(error.Message, error);
    }

    private static SD.Bitmap Solid(int w, int h, SD.Color c)
    {
        var b = new SD.Bitmap(w, h, SD.Imaging.PixelFormat.Format32bppPArgb);
        using var g = SD.Graphics.FromImage(b);
        g.Clear(c);
        return b;
    }

    // ------------------------------------------------------------------
    // A. The first launch: the welcome bubble and the first capture's card
    //    both take the bottom-right corner, and the card is wholly inside the
    //    bubble on a 150 % primary — covering its "Got it" button. The bubble
    //    retires when a capture lands, which is all it was asking for.
    // ------------------------------------------------------------------

    [Fact]
    public void A_WelcomeBubbleAndTheFirstCardDoNotShareTheCorner()
    {
        RunSta(() =>
        {
            SD.Rectangle area = WF.Screen.PrimaryScreen!.WorkingArea;
            // App.TrayAnchorPoint's own fallback: the notification-area corner.
            var trayAnchor = new SD.Point(area.Right - 60, area.Bottom);

            var actions = new List<FirstRunWelcomeWindow.WelcomeAction>
            {
                new("A", "Capture a region", "Ctrl+Shift+1", () => { }),
                new("B", "Record the screen", "Ctrl+Shift+3", () => { }),
                new("C", "Extract text", "Ctrl+Shift+4", () => { }),
            };

            using var welcome = new FirstRunWelcomeWindow(trayAnchor, actions, () => { });
            welcome.Show();
            PumpFor(500);
            SD.Rectangle bubble = welcome.Bounds;

            using var card = new FastQuickActionsWindow(Solid(400, 300, SD.Color.Coral), new SettingsService());
            card.Show();
            PumpFor(900);
            SD.Rectangle cardBounds = card.Bounds;

            Assert.True(
                bubble.IntersectsWith(cardBounds),
                "the premise of this probe: the card lands on the bubble's corner");
            Assert.True(
                FirstRunWelcomeWindow.LiveBounds.IsEmpty,
                $"the onboarding bubble at {bubble} is still up under the card at {cardBounds}, " +
                "with its Got it button covered");
        });
    }

    // ------------------------------------------------------------------
    // B. RegisterHotkeysCore drops a duplicate gesture silently. If two
    //    DEFAULTS ever collide, one shipped action is dead with no warning.
    // ------------------------------------------------------------------

    [Fact]
    public void B_EveryDefaultHotkeyGestureIsDistinct()
    {
        var s = new Settings();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo p in typeof(Settings).GetProperties()
                     .Where(p => p.Name.StartsWith("Hotkey", StringComparison.Ordinal) &&
                                 p.PropertyType == typeof(string)))
        {
            string gesture = (string?)p.GetValue(s) ?? "";
            if (string.IsNullOrWhiteSpace(gesture)) continue;
            Assert.True(
                HotkeyManager.TryParseGesture(gesture, out var mods, out var key),
                $"{p.Name} default '{gesture}' does not parse, so it registers nothing");
            string claim = $"{(int)mods}:{(int)key}";
            Assert.False(seen.ContainsKey(claim),
                $"{p.Name} and {seen.GetValueOrDefault(claim)} both default to '{gesture}'; " +
                "RegisterHotkeysCore's dedup drops the second one silently");
            seen[claim] = p.Name;
        }
    }

    // ------------------------------------------------------------------
    // C. A history folder with 5,000 files. GetItems stats every file to
    //    sort by capture time; make sure that stays usable and correct.
    // ------------------------------------------------------------------

    [Fact]
    public void C_AHugeHistoryFolderStillEnumeratesInOrder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-breaker7", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService();
            settings.Current.HistoryLimit = 10_000;
            var history = new HistoryService(settings, () => dir);

            var start = new DateTime(2026, 1, 1, 0, 0, 0);
            for (int i = 0; i < 5000; i++)
                File.WriteAllBytes(
                    Path.Combine(dir, $"{start.AddSeconds(i):yyyyMMdd-HHmmss-fff}.png"), []);
            // One renamed file with no timestamp prefix — the case the sort comment calls out.
            File.WriteAllBytes(Path.Combine(dir, "1099 tax form.png"), []);

            var sw = Stopwatch.StartNew();
            List<string> items = history.GetItems();
            sw.Stop();

            Assert.Equal(5001, items.Count);
            Assert.True(sw.ElapsedMilliseconds < 5000,
                $"GetItems over 5,001 files took {sw.ElapsedMilliseconds} ms on the UI path");
            // The renamed file has no timestamp prefix, so it sorts by write time (now) and
            // is legitimately newest. Everything else is newest-first by capture time.
            Assert.Equal("1099 tax form.png", Path.GetFileName(items[0]));
            Assert.Equal($"{start.AddSeconds(4999):yyyyMMdd-HHmmss-fff}.png", Path.GetFileName(items[1]));
            Assert.Equal($"{start:yyyyMMdd-HHmmss-fff}.png", Path.GetFileName(items[^1]));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // D. An empty history folder, and one that does not exist at all.
    // ------------------------------------------------------------------

    [Fact]
    public void D_EmptyAndMissingHistoryFoldersAreNotAnError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "winshot-breaker7-missing", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService();
        var history = new HistoryService(settings, () => missing);
        Assert.Empty(history.GetItems());
        history.PruneByAge(30);           // must not create the folder or throw
        Assert.False(Directory.Exists(missing));

        string empty = Path.Combine(Path.GetTempPath(), "winshot-breaker7-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var h2 = new HistoryService(settings, () => empty);
            Assert.Empty(h2.GetItems());
            // File.Delete on a missing file is a no-op, so this reports success.
            Assert.True(h2.Delete(Path.Combine(empty, "nope.png")).Succeeded);
        }
        finally { try { Directory.Delete(empty, true); } catch { } }
    }

    // ------------------------------------------------------------------
    // E. The card's low-level keyboard hook must never outlive the card
    //    being taken off screen — a stuck WH_KEYBOARD_LL is system-wide.
    // ------------------------------------------------------------------

    [Fact]
    public void E_TheCardsKeyboardHookIsGoneWhenTheCardLeavesTheScreen()
    {
        RunSta(() =>
        {
            using var card = new FastQuickActionsWindow(Solid(400, 300, SD.Color.Coral), new SettingsService());
            card.Show();
            PumpFor(400);

            Call(card, "SetHovering", true);
            PumpFor(50);
            Assert.NotEqual(IntPtr.Zero, (IntPtr)Fld(card, "_keyHook")!);

            // The annotate hand-off / "temporarily hide" path.
            card.StepAside();
            PumpFor(500);
            Assert.Equal(IntPtr.Zero, (IntPtr)Fld(card, "_keyHook")!);

            card.ComeBack();
            PumpFor(500);
            Assert.Equal(IntPtr.Zero, (IntPtr)Fld(card, "_keyHook")!);
        });
    }

    // ------------------------------------------------------------------
    // H. Same root cause as A: corner arbitration only knows about the
    //    quick-actions stack, so an unanchored toast lands on the bubble too.
    //    A first launch whose hotkeys are taken raises exactly that toast.
    // ------------------------------------------------------------------

    [Fact]
    public void H_ACornerToastNeverLandsOnTheOnboardingBubble()
    {
        RunSta(() =>
        {
            SD.Rectangle area = WF.Screen.PrimaryScreen!.WorkingArea;
            var trayAnchor = new SD.Point(area.Right - 60, area.Bottom);
            var actions = new List<FirstRunWelcomeWindow.WelcomeAction>
            {
                new("A", "Capture a region", "Ctrl+Shift+1", () => { }),
                new("B", "Record the screen", "Ctrl+Shift+3", () => { }),
                new("C", "Extract text", "Ctrl+Shift+4", () => { }),
            };

            using var welcome = new FirstRunWelcomeWindow(trayAnchor, actions, () => { });
            welcome.Show();
            PumpFor(500);
            SD.Rectangle bubble = welcome.Bounds;

            using var toast = new ToastWindow(
                "Some hotkeys unavailable",
                "3 shortcuts are in use by other apps — see Settings > Shortcuts.",
                null, null, ToastSeverity.Error);
            toast.Show();
            PumpFor(500);

            Assert.False(
                bubble.IntersectsWith(toast.Bounds),
                $"the startup error toast at {toast.Bounds} covers the onboarding bubble at {bubble}");
        });
    }

    // ------------------------------------------------------------------
    // G. SetTextToClipboardAsync gives up with a BARE ExternalException,
    //    not a COMException — so a caller catching COMException misses it.
    // ------------------------------------------------------------------

    [Fact]
    public void G_ABareExternalExceptionFromTheClipboardIsStillClipboardAdvice()
    {
        var busy = new System.Runtime.InteropServices.ExternalException("CLIPBRD_E_CANT_OPEN")
        {
            HResult = unchecked((int)0x800401D0),
        };
        Assert.Contains("clipboard", FriendlyError.For(busy), StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<System.Runtime.InteropServices.COMException>(busy);
    }
}

/// <summary>
/// F. An old settings.json that predates the matrix AND the first-run flag. Separate class so
/// it can join the settings-persistence collection (DirOverride is process-wide state).
/// </summary>
[Collection("SettingsPersistence")]
public class BreakerProbesRound7Settings
{
    [Theory]
    [InlineData("copy", true, false, false, false, false)]
    [InlineData("save", false, true, false, false, false)]
    [InlineData("edit", false, false, true, false, false)]
    [InlineData("pin", false, false, false, true, false)]
    [InlineData("background", false, false, false, false, true)]
    [InlineData("overlay", false, false, false, false, true)]
    public void F_AnOldSingleActionSurvivesTheMatrixMigration(
        string action, bool copy, bool save, bool annotate, bool pin, bool overlay)
    {
        string dir = Path.Combine(Path.GetTempPath(), "winshot-breaker7-set", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string? previous = SettingsService.DirOverride;
        try
        {
            SettingsService.DirOverride = dir;
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                $$"""
                { "PostCaptureAction": "{{action}}", "AutoCopyToClipboard": false }
                """);

            var svc = new SettingsService();
            svc.Load();
            Settings s = svc.Current;

            Assert.True(s.HasCompletedFirstRun, "an upgrading install was re-onboarded");
            Assert.Equal(copy, s.ScreenshotCopy);
            Assert.Equal(save, s.ScreenshotSave);
            Assert.Equal(annotate, s.ScreenshotOpenAnnotate);
            Assert.Equal(pin, s.ScreenshotPin);
            Assert.Equal(overlay, s.ScreenshotShowOverlay);
            Assert.Equal(PostCaptureMigration.MatrixSchemaVersion, s.SettingsSchemaVersion);
        }
        finally
        {
            SettingsService.DirOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
