using WinShot.History;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// What a drag out of the history grid carries. A multi-selection has to leave as one drop —
/// dragging it a file at a time was the whole complaint — but a selection the user made and
/// then walked away from must not attach itself to an unrelated tile.
/// </summary>
public class HistoryDragPathsTests
{
    private const string A = @"C:\history\20260803-120000-000.png";
    private const string B = @"C:\history\20260803-120001-000.png";
    private const string C = @"C:\history\20260803-120002-000.png";

    [Fact]
    public void OutsideSelectionMode_DragsOnlyTheGrabbedTile()
    {
        Assert.Equal(new[] { A }, HistoryWindow.DragPaths(A, Array.Empty<string>()));
    }

    [Fact]
    public void GrabbingASelectedTile_DragsTheWholeSelection()
    {
        Assert.Equal(new[] { A, B, C }, HistoryWindow.DragPaths(B, new[] { A, B, C }));
    }

    [Fact]
    public void GrabbingATileOutsideTheSelection_DragsOnlyThatTile()
    {
        Assert.Equal(new[] { C }, HistoryWindow.DragPaths(C, new[] { A, B }));
    }

    /// <summary>Windows paths do not care about case, so neither may the membership test —
    /// otherwise the same tile drags as one file or three depending on the drive letter.</summary>
    [Fact]
    public void SelectionMembershipIgnoresCase()
    {
        Assert.Equal(new[] { A, B }, HistoryWindow.DragPaths(B.ToUpperInvariant(), new[] { A, B }));
    }
}
