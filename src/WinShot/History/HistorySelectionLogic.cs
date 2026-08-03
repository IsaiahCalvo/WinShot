namespace WinShot.History;

internal readonly record struct HistorySelectionChange(int Start, int End, bool Select, int Anchor);

internal static class HistorySelectionLogic
{
    public static bool ShouldExitSelectionAfterDelete(int remainingItemCount) => remainingItemCount <= 0;

    public static HistorySelectionChange ResolveClick(
        int itemCount,
        int anchorIndex,
        int targetIndex,
        bool shift,
        bool targetIsSelected)
    {
        if (targetIndex < 0 || targetIndex >= itemCount)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));

        if (shift && anchorIndex >= 0 && anchorIndex < itemCount)
        {
            return new HistorySelectionChange(
                Math.Min(anchorIndex, targetIndex),
                Math.Max(anchorIndex, targetIndex),
                Select: !targetIsSelected,
                Anchor: anchorIndex);
        }

        return new HistorySelectionChange(
            targetIndex,
            targetIndex,
            Select: !targetIsSelected,
            Anchor: targetIndex);
    }
}
