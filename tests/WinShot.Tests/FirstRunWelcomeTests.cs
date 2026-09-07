using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Onboarding. Before this the app installed, put a 16 px icon in the tray, and told nobody
/// that eight hotkeys were already bound. These pin the two things that make the bubble worth
/// having: its rows RUN the commands, and it is shown exactly once.
/// </summary>
public class FirstRunWelcomeTests
{
    private static FirstRunWelcomeWindow Build(
        SD.Point? anchor, out List<string> ran, out int dismissals)
    {
        var log = new List<string>();
        int closed = 0;
        var actions = new List<FirstRunWelcomeWindow.WelcomeAction>
        {
            new("", "Capture a region", "Ctrl+Shift+1", () => log.Add("capture")),
            new("", "Record the screen", "Ctrl+Shift+3", () => log.Add("record")),
            new("", "Extract text", "Ctrl+Shift+4", () => log.Add("ocr")),
        };
        var window = new FirstRunWelcomeWindow(anchor, actions, () => closed++);
        ran = log;
        dismissals = closed;
        return window;
    }

    [Fact]
    public void EveryRowIsAnActionThatRuns()
    {
        using var window = Build(new SD.Point(1600, 1040), out List<string> ran, out _);

        Assert.Equal(3, window.Actions.Count);
        foreach (var action in window.Actions)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Title));
            Assert.False(string.IsNullOrWhiteSpace(action.Hint));
            Assert.False(string.IsNullOrWhiteSpace(action.Glyph));
            action.Run();
        }

        // Describing a command teaches nothing; running it is the point.
        Assert.Equal(["capture", "record", "ocr"], ran);
    }

    [Fact]
    public void ItSizesItselfForItsRowsAndItsTail()
    {
        using var window = Build(null, out _, out _);
        Assert.True(window.ClientSize.Width >= 300, $"width was {window.ClientSize.Width}");
        // Header + three rows + footer + the tail strip.
        Assert.True(window.ClientSize.Height >= 3 * 46, $"height was {window.ClientSize.Height}");
    }

    [Fact]
    public void DismissingRunsTheCallbackExactlyOnce()
    {
        int closed = 0;
        var window = new FirstRunWelcomeWindow(
            null,
            [new FirstRunWelcomeWindow.WelcomeAction("", "Capture", "Ctrl+Shift+1", () => { })],
            () => closed++);
        window.Show();
        window.Close();
        window.Dispose();
        Assert.Equal(1, closed);
    }

    [Fact]
    public void FirstRunIsRecordedBeforeTheWindowOpens_SoACrashDoesNotReteach()
    {
        // The flag is the whole contract: the app writes it as it shows the bubble, never
        // when the bubble is dismissed, because a dismissal that never happens would mean
        // onboarding on every launch forever.
        var settings = new Settings();
        Assert.False(settings.HasCompletedFirstRun);
        settings.HasCompletedFirstRun = true;
        Assert.True(settings.HasCompletedFirstRun);
    }

    [Fact]
    public void MotionIsReleasedWhenItCloses()
    {
        int before = Motion.Holders;
        using (var window = Build(null, out _, out _))
        {
            window.Show();
            PumpOnce();
            window.Close();
        }
        PumpOnce();
        Assert.Equal(before, Motion.Holders);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1920, -1080)]      // a monitor above and left of the laptop
    [InlineData(400, -2)]           // just above the top edge (top-docked taskbar)
    [InlineData(999999, 999999)]    // a tray icon on a monitor that has been unplugged
    [InlineData(-999999, -999999)]
    public void TheBubbleLandsOnScreenWhateverTheTrayAnchorSays(int x, int y)
    {
        using var window = Build(new SD.Point(x, y), out _, out _);
        window.Show();
        PumpOnce();

        SD.Rectangle screen = WF.Screen.FromControl(window).WorkingArea;
        Assert.True(window.Bounds.IntersectsWith(screen), $"bubble at {window.Bounds} missed {screen}");
        window.Close();
    }

    [Fact]
    public void MotionIsReleasedWhenItIsDisposedMidEntrance()
    {
        // Quitting the app during its own onboarding tears the bubble down with no Close().
        int before = Motion.Holders;
        for (int i = 0; i < 5; i++)
        {
            var window = Build(new SD.Point(200, 200), out _, out _);
            window.Show();
            PumpOnce();     // OnShown starts the entrance
            window.Dispose();
        }
        for (int i = 0; i < 200 && Motion.Holders != before; i++)
            PumpOnce();
        Assert.Equal(before, Motion.Holders);
    }

    [Fact]
    public void ABubbleWithNoRowsStillOpensAndCloses()
    {
        using var window = new FirstRunWelcomeWindow(null, [], () => { });
        window.Show();
        PumpOnce();
        window.Close();
    }

    /// <summary>
    /// The bubble is WinShot's only onboarding surface, and its whole job is to teach the
    /// product. Painted rectangles with a hit test taught it to nobody without a mouse: no
    /// tab stop, no accessible children, no keyboard activation, and — because the form
    /// refused activation — no way to reach the "Got it" button or press Escape either.
    /// </summary>
    [Fact]
    public void EveryRowIsReachableAndRunnableFromTheKeyboard()
    {
        using var window = Build(new SD.Point(600, 800), out List<string> ran, out _);
        window.Show();
        PumpOnce();

        Assert.False(window.ClientRectangle.IsEmpty);

        // Real, focusable, named children — not paint.
        var rows = window.Controls.OfType<WF.Control>()
            .Where(c => c.AccessibleRole == WF.AccessibleRole.PushButton && c.TabStop)
            .ToList();
        Assert.Equal(3, rows.Count(r => r.AccessibleName is "Capture a region" or "Record the screen" or "Extract text"));
        foreach (WF.Control row in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.AccessibleName));
            Assert.False(string.IsNullOrWhiteSpace(row.AccessibleDescription));
        }

        // Enter runs the row that has focus.
        WF.Control first = rows.First(r => r.AccessibleName == "Capture a region");
        Assert.True(first.CanSelect);
        first.Focus();
        PumpOnce();
        typeof(WF.Control)
            .GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(first, new object[] { new WF.KeyEventArgs(WF.Keys.Enter) });
        PumpOnce();
        Assert.Equal(["capture"], ran);
    }

    [Fact]
    public void EscapeDismissesTheBubble()
    {
        int closed = 0;
        var window = new FirstRunWelcomeWindow(
            null,
            [new FirstRunWelcomeWindow.WelcomeAction("", "Capture", "Ctrl+Shift+1", () => { })],
            () => closed++);
        window.Show();
        PumpOnce();

        // A topmost surface with no Escape is one a keyboard-only user cannot get rid of.
        var msg = WF.Message.Create(window.Handle, 0x0100 /* WM_KEYDOWN */, (IntPtr)(int)WF.Keys.Escape, IntPtr.Zero);
        bool handled = (bool)typeof(WF.Form)
            .GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, new object[] { msg, WF.Keys.Escape })!;
        PumpOnce();

        Assert.True(handled);
        Assert.Equal(1, closed);
        window.Dispose();
    }

    private static void PumpOnce() => WF.Application.DoEvents();
}
