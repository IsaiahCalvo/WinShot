using System.Drawing;
using WF = System.Windows.Forms;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

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
/// DwmGetWindowAttribute, physical pixels) and the client rect (GetClientRect + ClientToScreen,
/// virtualized to logical pixels) come back in different coordinate spaces. That is a harness
/// artifact — the real app is PerMonitorV2 via app.manifest and both are physical — but a
/// coordinate assertion here would lie in both directions.
/// </summary>
public class SnapScanWindowTests
{
    private static WF.Form ShowProbeWindow(Rectangle bounds, out WF.Panel outer, out WF.Button inner)
    {
        var form = new WF.Form
        {
            FormBorderStyle = WF.FormBorderStyle.Sizable,
            StartPosition = WF.FormStartPosition.Manual,
            ShowInTaskbar = false,
            Text = "SnapScanProbeWindow",
            Bounds = bounds,
        };
        outer = new WF.Panel { Bounds = new Rectangle(10, 10, 300, 200) };
        inner = new WF.Button { Bounds = new Rectangle(20, 20, 120, 40), Text = "inner" };
        outer.Controls.Add(inner);
        form.Controls.Add(outer);
        form.Show();
        Pump();

        // Showing a window is asynchronous — DWM has to compose it before the walk can see it,
        // and the suite runs other window-creating tests alongside this one. Wait for the frame
        // rect to actually turn up rather than guessing a message-pump count. A walk that has
        // genuinely stopped emitting the frame still fails, just a second later.
        var deadline = System.DateTime.UtcNow.AddSeconds(5);
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
        using var form = ShowProbeWindow(new Rectangle(120, 140, 500, 400), out var outer, out var inner);
        var rects = WindowEnumerator.GetSnapRectangles();

        int frame = rects.FindIndex(r => r.Handle == form.Handle && r.IsWindow);
        int client = rects.FindIndex(r => r.Handle == form.Handle && !r.IsWindow);
        int outerIndex = rects.FindIndex(r => r.Handle == outer.Handle);
        int innerIndex = rects.FindIndex(r => r.Handle == inner.Handle);

        Assert.True(frame >= 0, $"the form's frame rect is missing; list holds {rects.Count}");
        Assert.True(innerIndex >= 0, "the inner button is missing");
        Assert.True(outerIndex >= 0, "the outer panel is missing");

        // Hover takes the first rect containing the cursor, so anything nested has to be listed
        // before whatever encloses it or element snapping degrades to window snapping.
        Assert.True(innerIndex < outerIndex, $"inner({innerIndex}) must precede outer({outerIndex})");
        Assert.True(outerIndex < frame, $"outer({outerIndex}) must precede frame({frame})");
        if (client >= 0)
            Assert.True(client < frame, $"client({client}) must precede frame({frame})");

        Assert.Single(rects, r => r.Handle == form.Handle && r.IsWindow);
    }

    [Fact]
    public void ExcludingAWindowDropsItsClientAndControlRectsToo()
    {
        using var form = ShowProbeWindow(new Rectangle(240, 240, 400, 300), out var outer, out var inner);
        var rects = WindowEnumerator.GetSnapRectangles(new HashSet<IntPtr> { form.Handle });

        Assert.DoesNotContain(rects, r => r.Handle == form.Handle);
        Assert.DoesNotContain(rects, r => r.Handle == outer.Handle);
        Assert.DoesNotContain(rects, r => r.Handle == inner.Handle);
    }

    [Fact]
    public void AWindowClosedBeforeTheWalkIsSimplyAbsent()
    {
        var form = ShowProbeWindow(new Rectangle(220, 220, 400, 300), out _, out _);
        IntPtr handle = form.Handle;
        form.Dispose();
        Pump();

        var rects = WindowEnumerator.GetSnapRectangles();

        Assert.DoesNotContain(rects, r => r.Handle == handle);
    }
}
