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
/// That split is not only a distortion, it can *remove* handles: AddControl clips each child to
/// its parent frame, and a logical child rect far enough outside a physical frame rect clips to
/// empty and is never emitted. The probe therefore sits at the primary origin, where the two
/// spaces cannot diverge: at scale s the frame spans physical 0..500s while the outermost child
/// sits at logical 18..318, so the child stays inside the frame for every s >= 1. Move the probe
/// away from the origin and the presence assertions below start depending on the monitor's
/// scale factor.
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

    [Fact]
    public void ExcludingAWindowDropsItsClientAndControlRectsToo()
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

    [Fact]
    public void AnExhaustedBudgetStopsTheWalkInsteadOfRunningItAnyway()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rects = WindowEnumerator.GetSnapRectangles(timeoutMs: 0);
        clock.Stop();

        Assert.NotNull(rects);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"budget ignored: {clock.ElapsedMilliseconds} ms");
    }
}
