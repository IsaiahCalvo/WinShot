using System.Collections.ObjectModel;

namespace WinShot.History;

/// <summary>
/// Keeps the full, lightweight history model in memory while exposing only a bounded
/// page to WPF. The grouped WrapPanel therefore creates at most one page of cards at
/// startup instead of a card for every retained file.
/// </summary>
public sealed class HistoryIncrementalCollection
{
    public const int DefaultPageSize = 200;

    private readonly int _pageSize;
    private List<HistoryItem> _allItems = new();
    private List<HistoryItem> _filteredItems = new();

    public HistoryIncrementalCollection(int pageSize = DefaultPageSize)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        _pageSize = pageSize;
    }

    public ObservableCollection<HistoryItem> VisibleItems { get; } = new();
    public IReadOnlyList<HistoryItem> AllItems => _allItems;
    public int TotalCount => _allItems.Count;
    public int FilteredCount => _filteredItems.Count;
    public int VisibleCount => VisibleItems.Count;
    public bool HasMore => VisibleCount < FilteredCount;

    /// <summary>
    /// Atomically replaces the backing model. Cancellation is checked while the candidate
    /// list is built, so a superseded watcher refresh cannot partially clear the visible UI.
    /// The previous page depth and selected path are retained when they still exist.
    /// </summary>
    public HistoryItem? ReplaceAll(
        IEnumerable<HistoryItem> items,
        Func<HistoryItem, bool> filter,
        string? selectedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(filter);

        var candidateAll = new List<HistoryItem>();
        foreach (HistoryItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateAll.Add(item);
        }

        var candidateFiltered = new List<HistoryItem>();
        foreach (HistoryItem item in candidateAll)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter(item))
                candidateFiltered.Add(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int targetCount = PageTarget(candidateFiltered, selectedPath, Math.Max(_pageSize, VisibleCount));

        _allItems = candidateAll;
        _filteredItems = candidateFiltered;
        ReplaceVisible(targetCount);
        return FindVisible(selectedPath);
    }

    /// <summary>Applies a local filter without expanding beyond the current page depth.</summary>
    public HistoryItem? ApplyFilter(Func<HistoryItem, bool> filter, string? selectedPath)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filteredItems = _allItems.Where(filter).ToList();
        int targetCount = PageTarget(_filteredItems, selectedPath, Math.Max(_pageSize, VisibleCount));
        ReplaceVisible(targetCount);
        return FindVisible(selectedPath);
    }

    /// <summary>Appends one bounded page and returns only the newly visible items.</summary>
    public IReadOnlyList<HistoryItem> LoadNextPage()
    {
        if (!HasMore)
            return Array.Empty<HistoryItem>();

        int start = VisibleCount;
        int end = Math.Min(start + _pageSize, FilteredCount);
        var added = new List<HistoryItem>(end - start);
        for (int index = start; index < end; index++)
        {
            HistoryItem item = _filteredItems[index];
            VisibleItems.Add(item);
            added.Add(item);
        }

        return added;
    }

    /// <summary>
    /// Removes an item and backfills the visible page so a delete does not shrink a loaded
    /// page while more matching history remains.
    /// </summary>
    public void Remove(HistoryItem item)
    {
        int priorVisibleCount = VisibleCount;
        _allItems.Remove(item);
        _filteredItems.Remove(item);
        VisibleItems.Remove(item);

        int target = Math.Min(priorVisibleCount, FilteredCount);
        while (VisibleItems.Count < target)
            VisibleItems.Add(_filteredItems[VisibleItems.Count]);
    }

    public void Clear()
    {
        _allItems.Clear();
        _filteredItems.Clear();
        VisibleItems.Clear();
    }

    private int PageTarget(IReadOnlyList<HistoryItem> items, string? selectedPath, int requestedCount)
    {
        int target = Math.Min(Math.Max(_pageSize, requestedCount), items.Count);
        if (string.IsNullOrWhiteSpace(selectedPath))
            return target;

        int selectedIndex = -1;
        for (int index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].FilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < target)
            return target;

        int requiredPages = (selectedIndex / _pageSize) + 1;
        return Math.Min(requiredPages * _pageSize, items.Count);
    }

    private void ReplaceVisible(int count)
    {
        VisibleItems.Clear();
        for (int index = 0; index < count; index++)
            VisibleItems.Add(_filteredItems[index]);
    }

    private HistoryItem? FindVisible(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return VisibleItems.FirstOrDefault(item =>
            string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }
}
