using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistorySelectionLogicTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void SelectionModeExit_AfterDelete_DependsOnRemainingHistory(int remaining, bool expected)
    {
        Assert.Equal(expected, HistorySelectionLogic.ShouldExitSelectionAfterDelete(remaining));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void IndividualClick_TogglesOnlyTheClickedItem(bool shift, bool selected, bool expectedSelection)
    {
        HistorySelectionChange change = HistorySelectionLogic.ResolveClick(10, anchorIndex: 2, targetIndex: 7, shift, selected);

        Assert.Equal(7, change.Start);
        Assert.Equal(7, change.End);
        Assert.Equal(expectedSelection, change.Select);
        Assert.Equal(7, change.Anchor);
    }

    [Fact]
    public void ShiftClick_SelectsTheConsecutiveRangeFromTheAnchor()
    {
        HistorySelectionChange change = HistorySelectionLogic.ResolveClick(10, anchorIndex: 2, targetIndex: 7, shift: true, targetIsSelected: false);

        Assert.Equal(2, change.Start);
        Assert.Equal(7, change.End);
        Assert.True(change.Select);
        Assert.Equal(2, change.Anchor);
    }

    [Fact]
    public void ShiftClickOnSelectedTarget_DeselectsTheConsecutiveRange()
    {
        HistorySelectionChange change = HistorySelectionLogic.ResolveClick(10, anchorIndex: 7, targetIndex: 2, shift: true, targetIsSelected: true);

        Assert.Equal(2, change.Start);
        Assert.Equal(7, change.End);
        Assert.False(change.Select);
        Assert.Equal(7, change.Anchor);
    }
}
