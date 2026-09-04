using System.Drawing;
using WF = System.Windows.Forms;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// These tests publish real top-level windows on the shared test desktop, so they run in their
/// own non-parallel collection rather than alongside the render harnesses and the desktop-wide
/// snap invariants.
/// </summary>
[CollectionDefinition("SnapProbeWindows", DisableParallelization = true)]
public sealed class SnapProbeWindowCollection
{
}

/// <summary>
/// The snap walk, exercised against a window this test creates itself.
///
/// SnapRectangleListTests asserts invariants over whatever the desktop happens to hold, and the
/// hidden test desktop holds nothing — so those loops run zero times and guard nothing. These
/// tests put a real form with nested controls on the desktop so the walk actually has something
/// to find.
///
/// Assert ordering and handle identity only, never coordinates. The test host carries no
/// manifest so it is DPI-unaware, while DWM is not: on a scaled monitor the frame rect (from
/// DwmGetWindowAttribute, physical pixels) and everything read through GetWindowRect /
/// GetClientRect (virtualized to logical pixels) come back in different coordinate spaces. That
/// is a harness artifact — the real app is PerMonitorV2 via app.manifest and both are physical.
///
/// That split is not only a distortion, it can *remove* handles: CollectChild clips each child to
/// its parent frame, and a logical child rect far enough outside a physical frame rect clips to
/// empty and is never emitted. The probe therefore sits at the primary origin, where the two
/// spaces cannot diverge: at scale s the frame spans physical 0..500s while the outermost child
/// sits at logical 18..318, so the child stays inside the frame for every s >= 1. Move the probe
/// away from the origin and the presence assertions below start depending on the monitor's
/// scale factor: at (0, -350) on a monitor above the laptop the walk emits the frame and a
/// clipped outer panel and loses the inner button entirely.
///
/// So the breaker checklist's "negative-Y and mixed-DPI monitors" is UNCOVERED for the snap
/// walk, not merely awkward to write. It stays uncovered until the test host gets its own
/// PerMonitorV2 manifest; do not read the origin-pinned probe below as having checked it.
///
/// These forms are also built and shown on whatever thread xUnit hands the test, which is MTA.
/// It works for a Form/Panel/Button that only gets shown and measured, but WinForms on MTA is
/// unsupported for the apartment-sensitive paths, so do not grow a clipboard, drag-drop or
/// common-dialog assertion onto this probe.
/// </summary>
[Collection("SnapProbeWindows")]
public class SnapScanWindowTests
{
    private static readonly Rectangle ProbeBounds = new(0, 0, 500, 400);

    private static WF.Form ShowProbeWindow(out WF.Panel outer, out WF.Button inner)
    {
        var form = new WF.Form
        {
            FormBorderStyle = WF.FormBorderStyle.Sizable,
            StartPosition = WF.FormStartPosition.Manual,
            ShowInTaskbar = false,
            Text = "SnapScanProbeWindow",
            Bounds = ProbeBounds,
            // TopMost so nothing can settle in front of the probe. DropCoveredControls drops a
            // control rect covered by ANY earlier-accepted rect, including an unrelated window's
            // frame — correct behaviour (you cannot point at a covered control) but it would
            // silently delete the outer/inner rects these tests assert on. The frame rect
            // survives coverage either way, so the wait loop below would not notice.
            TopMost = true,
        };
        outer = new WF.Panel { Bounds = new Rectangle(10, 10, 300, 200) };
        inner = new WF.Button { Bounds = new Rectangle(20, 20, 120, 40), Text = "inner" };
        outer.Controls.Add(inner);
        form.Controls.Add(outer);
        form.Show();
        Pump();

        // Showing a window is asynchronous — DWM has to compose it before the walk can see it.
        // Wait for the frame rect to actually turn up rather than guessing a message-pump count.
        // A walk that has genuinely stopped emitting the frame still fails, just a moment later.
        var deadline = System.DateTime.UtcNow.AddSeconds(3);
        while (System.DateTime.UtcNow < deadline
               && !WindowEnumerator.GetSnapRectangles().Exists(r => r.Handle == form.Handle && r.IsWindow))
        {
            Pump();
        }
        return form;
    }

    private static void Pump()
    {
        for (int i = 0; i < 20; i++) WF.Application.DoEvents();
        System.Threading.Thread.Sleep(15);
    }

    [Fact]
    public void ChildControlsComeBeforeTheClientAreaWhichComesBeforeTheFrame()
    {
        using var form = ShowProbeWindow(out var outer, out var inner);
        var rects = WindowEnumerator.GetSnapRectangles();

        int frame = rects.FindIndex(r => r.Handle == form.Handle && r.IsWindow);
        int client = rects.FindIndex(r => r.Handle == form.Handle && !r.IsWindow);
        int outerIndex = rects.FindIndex(r => r.Handle == outer.Handle);
        int innerIndex = rects.FindIndex(r => r.Handle == inner.Handle);

        Assert.True(frame >= 0, $"the form's frame rect is missing; list holds {rects.Count}");
        Assert.True(innerIndex >= 0, "the inner button is missing");
        Assert.True(outerIndex >= 0, "the outer panel is missing");

        // The client rung is what makes element snapping finer than window snapping: it is the
        // rung the hover glide lands on inside a window's content but over no child HWND, which
        // is every rect Chromium, Electron and WPF windows have to offer. The probe is Sizable,
        // so its client area can never equal its frame and this rect is always emitted.
        Assert.True(client >= 0, "the form's client-area rect is missing");

        // Hover takes the first rect containing the cursor, so anything nested has to be listed
        // before whatever encloses it or element snapping degrades to window snapping.
        Assert.True(innerIndex < outerIndex, $"inner({innerIndex}) must precede outer({outerIndex})");
        Assert.True(outerIndex < frame, $"outer({outerIndex}) must precede frame({frame})");
        Assert.True(client < frame, $"client({client}) must precede frame({frame})");

        Assert.Single(rects, r => r.Handle == form.Handle && r.IsWindow);
    }

    /// <summary>
    /// Exclusion is only transitive for a TOP-LEVEL handle, which is the only kind the one
    /// production caller (FastRegionSelectorDialog) ever passes: the top-level pass drops the
    /// window before it descends, so the whole subtree goes with it. Excluding a mid-tree
    /// control does NOT take its children — EnumChildWindows enumerates every descendant
    /// directly off the top-level window, so the skipped control is never on the path to them.
    /// If you reach for `exclude` with a control handle, expect its children back.
    /// </summary>
    [Fact]
    public void ExcludingATopLevelWindowDropsItsClientAndControlRectsToo()
    {
        using var form = ShowProbeWindow(out var outer, out var inner);

        // Establish the handles are there to be dropped. Without this the exclusion assertions
        // below pass trivially for any handle the walk never emitted in the first place.
        var unfiltered = WindowEnumerator.GetSnapRectangles();
        Assert.Contains(unfiltered, r => r.Handle == form.Handle);
        Assert.Contains(unfiltered, r => r.Handle == outer.Handle);
        Assert.Contains(unfiltered, r => r.Handle == inner.Handle);

        var rects = WindowEnumerator.GetSnapRectangles(new HashSet<IntPtr> { form.Handle });

        Assert.DoesNotContain(rects, r => r.Handle == form.Handle);
        Assert.DoesNotContain(rects, r => r.Handle == outer.Handle);
        Assert.DoesNotContain(rects, r => r.Handle == inner.Handle);
    }

    [Fact]
    public void AWindowClosedBeforeTheWalkIsSimplyAbsent()
    {
        var form = ShowProbeWindow(out _, out _);
        IntPtr handle = form.Handle;
        form.Dispose();
        Pump();

        var rects = WindowEnumerator.GetSnapRectangles();

        Assert.DoesNotContain(rects, r => r.Handle == handle);
    }

    /// <summary>
    /// Guards the clock check the restructure relocated into the two collect entry points.
    ///
    /// The budget is compared against elapsed milliseconds, so a budget of 0 does NOT prove
    /// anything: the first callback reads 0 ms, `0 > 0` is false, and that window and its whole
    /// child walk run regardless — on a near-empty desktop an uncapped walk finishes inside a
    /// millisecond anyway, so a zero-budget test passes whether the check exists or not. A
    /// budget that is already exhausted at t = 0 is the only value that makes the check fire
    /// deterministically, and then the walk must emit nothing at all.
    /// </summary>
    [Fact]
    public void AnAlreadyExhaustedBudgetStopsTheWalkBeforeItEmitsAnything()
    {
        using var form = ShowProbeWindow(out _, out _);

        // Without this the assertion below is vacuous: an empty desktop yields an empty list.
        Assert.NotEmpty(WindowEnumerator.GetSnapRectangles());

        Assert.Empty(WindowEnumerator.GetSnapRectangles(timeoutMs: -1));
    }
}
