using System.Windows;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>Survey's group-resize scale rules (utils/groupResize.js resolveGroupResizeScale).</summary>
public class GroupResizeRuleTests
{
    [Fact]
    public void ACornerScalesBothAxesFreely()
    {
        var (sx, sy) = EditorWindow.ResolveGroupResizeScale(2, 1.5, 0.8, false, 0, 0);
        Assert.Equal(1.5, sx, 6);
        Assert.Equal(0.8, sy, 6);
    }

    [Fact]
    public void ASideScalesOnlyItsAxis()
    {
        var (sx, sy) = EditorWindow.ResolveGroupResizeScale(5, 2, 3, false, 0, 0);
        Assert.Equal(2, sx, 6);
        Assert.Equal(1, sy, 6);
        (sx, sy) = EditorWindow.ResolveGroupResizeScale(6, 2, 3, false, 0, 0);
        Assert.Equal(1, sx, 6);
        Assert.Equal(3, sy, 6);
    }

    [Fact]
    public void KeepingProportionsFollowsTheBiggerMove()
    {
        var (sx, sy) = EditorWindow.ResolveGroupResizeScale(2, 1.2, 2.0, true, 0, 0);
        Assert.Equal(2.0, sx, 6);
        Assert.Equal(2.0, sy, 6);
        // From a side, the other axis follows the moved one.
        (sx, sy) = EditorWindow.ResolveGroupResizeScale(5, 0.5, 9, true, 0, 0);
        Assert.Equal(0.5, sx, 6);
        Assert.Equal(0.5, sy, 6);
    }

    [Fact]
    public void AGroupNeverFlipsAndStopsAtItsFloor()
    {
        var (sx, sy) = EditorWindow.ResolveGroupResizeScale(2, -0.7, 0.01, false, 0.1, 0.2);
        Assert.Equal(0.1, sx, 6);
        Assert.Equal(0.2, sy, 6);
        // With proportions kept the floor is shared, so neither axis stops early.
        (sx, sy) = EditorWindow.ResolveGroupResizeScale(2, 0.01, 0.01, true, 0.1, 0.2);
        Assert.Equal(0.2, sx, 6);
        Assert.Equal(0.2, sy, 6);
    }

    [Fact]
    public void AnEdgeSnapsToANeighboursEdge()
    {
        var others = new[] { new Rect(100, 50, 40, 40) };
        Assert.Equal(3, ObjectSnap.EdgeOffset(97, others, axisX: true, tolerance: 6), 6);
        Assert.Equal(-2, ObjectSnap.EdgeOffset(142, others, axisX: true, tolerance: 6), 6);
        Assert.Equal(0, ObjectSnap.EdgeOffset(120, others, axisX: true, tolerance: 6), 6);
        Assert.Equal(1, ObjectSnap.EdgeOffset(89, others, axisX: false, tolerance: 6), 6);
    }

    [Fact]
    public void KScalesLineWidthsByTheSmallerScale()
    {
        Assert.Equal(6, EditorWindow.ScaledGroupLineWidth(3, 2, 2.5), 6);
        Assert.Equal(1.5, EditorWindow.ScaledGroupLineWidth(3, 0.5, 4), 6);
        Assert.Equal(0.1, EditorWindow.ScaledGroupLineWidth(1, 0.01, 0.01), 6);
        Assert.Equal(0, EditorWindow.ScaledGroupLineWidth(0, 2, 2), 6);
    }
}
