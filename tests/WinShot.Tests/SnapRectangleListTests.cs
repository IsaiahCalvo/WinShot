using System.Drawing;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Contract checks for the ShareX-style snap-rect list, run against whatever windows the
/// host desktop happens to have. The suite runs on a hidden desktop that can legitimately
/// be empty, so these assert invariants over the list rather than a minimum size.
/// </summary>
public class SnapRectangleListTests
{
    [Fact]
    public void InnerRectsPrecedeTheOuterRectOfTheSameWindow()
    {
        var rects = WindowEnumerator.GetSnapRectangles();

        // Hover takes the FIRST rect containing the cursor. A window listed before its own
        // client area or controls would swallow them, degrading element snapping to window
        // snapping — the exact failure this ordering exists to prevent.
        for (int i = 0; i < rects.Count; i++)
        {
            if (!rects[i].IsWindow)
                continue;
            for (int j = i + 1; j < rects.Count; j++)
            {
                if (rects[j].Handle == rects[i].Handle && rects[i].Bounds.Contains(rects[j].Bounds))
                    Assert.Fail($"Window rect {rects[i].Bounds} precedes its own inner rect {rects[j].Bounds}.");
            }
        }
    }

    [Fact]
    public void NoControlRectIsUnreachableBehindAnEarlierRect()
    {
        var rects = WindowEnumerator.GetSnapRectangles();

        for (int i = 0; i < rects.Count; i++)
        {
            if (rects[i].IsWindow)
                continue;
            for (int j = 0; j < i; j++)
            {
                Assert.False(rects[j].Bounds.Contains(rects[i].Bounds),
                    $"Control rect {rects[i].Bounds} is unreachable behind earlier rect {rects[j].Bounds}.");
            }
        }
    }

    [Fact]
    public void EveryRectIsNonEmpty()
    {
        foreach (var rect in WindowEnumerator.GetSnapRectangles())
            Assert.True(rect.Bounds.Width > 0 && rect.Bounds.Height > 0, $"Empty rect {rect.Bounds}.");
    }

    [Fact]
    public void ExcludedHandlesNeverAppear()
    {
        var all = WindowEnumerator.GetSnapRectangles();
        if (all.Count == 0)
            return; // bare desktop (the hidden test desktop) — nothing to exclude

        var victim = all[0].Handle;
        var filtered = WindowEnumerator.GetSnapRectangles(new HashSet<IntPtr> { victim });
        Assert.DoesNotContain(filtered, r => r.Handle == victim);
    }
}
