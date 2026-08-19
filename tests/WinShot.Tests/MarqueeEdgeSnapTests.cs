using WinShot.Capture;
using Xunit;

namespace WinShot.Tests;

public class MarqueeEdgeSnapTests
{
    // Sorted distinct edge coordinates, the shape SnapCoordinate is given at runtime.
    private static readonly int[] Edges = { 0, 100, 340, 341, 900 };

    [Theory]
    [InlineData(100, 100)]  // exactly on an edge
    [InlineData(103, 100)]  // just past one, within the 6 px pull
    [InlineData(95, 100)]   // just before one
    [InlineData(94, 100)]   // exactly at the tolerance
    [InlineData(906, 900)]  // past the last edge
    [InlineData(-6, 0)]     // before the first edge
    public void PullsOntoAnEdgeWithinTolerance(int value, int expected) =>
        Assert.Equal(expected, FastRegionSelectorDialog.SnapCoordinate(value, Edges));

    [Theory]
    [InlineData(93)]   // one px outside the pull
    [InlineData(200)]  // nowhere near anything
    [InlineData(908)]  // past the last edge and out of reach
    public void LeavesTheValueAloneBeyondTolerance(int value) =>
        Assert.Equal(value, FastRegionSelectorDialog.SnapCoordinate(value, Edges));

    [Fact]
    public void PicksTheNearerOfTwoCandidates()
    {
        // 344 sits 4 px from 340 and 3 px from 341 — both in reach, the closer one wins.
        Assert.Equal(341, FastRegionSelectorDialog.SnapCoordinate(344, Edges));
        Assert.Equal(340, FastRegionSelectorDialog.SnapCoordinate(336, Edges));
    }

    [Fact]
    public void NoEdgesMeansNoSnapping() =>
        Assert.Equal(517, FastRegionSelectorDialog.SnapCoordinate(517, Array.Empty<int>()));
}
