using System.IO;
using WinShot.History;
using Xunit;

namespace WinShot.Tests;

public class HistoryIncrementalCollectionTests
{
    [Fact]
    public void TwoHundredEntryLibrary_RealizesTheCompleteSinglePage()
    {
        var model = new HistoryIncrementalCollection();

        model.ReplaceAll(CreateItems(200), static _ => true, selectedPath: null);

        Assert.Equal(200, model.TotalCount);
        Assert.Equal(200, model.FilteredCount);
        Assert.Equal(200, model.VisibleCount);
        Assert.False(model.HasMore);
        Assert.All(model.VisibleItems, item => Assert.False(string.IsNullOrWhiteSpace(item.DayGroup)));
    }

    [Fact]
    public void FiveThousandEntryLibrary_RealizesOnlyBoundedPages()
    {
        var model = new HistoryIncrementalCollection();

        model.ReplaceAll(CreateItems(5_000), static _ => true, selectedPath: null);

        Assert.Equal(5_000, model.TotalCount);
        Assert.Equal(200, model.VisibleCount);
        Assert.True(model.HasMore);

        IReadOnlyList<HistoryItem> added = model.LoadNextPage();

        Assert.Equal(200, added.Count);
        Assert.Equal(400, model.VisibleCount);
        Assert.Equal(5_000, model.TotalCount);
        Assert.True(model.HasMore);
    }

    [Fact]
    public void Refresh_PreservesPageDepthAndSelectionByPath()
    {
        var model = new HistoryIncrementalCollection();
        List<HistoryItem> original = CreateItems(5_000);
        model.ReplaceAll(original, static _ => true, selectedPath: null);
        model.LoadNextPage();
        model.LoadNextPage();
        string selectedPath = original[450].FilePath;

        HistoryItem? restored = model.ReplaceAll(
            CreateItems(5_000),
            static _ => true,
            selectedPath);

        Assert.NotNull(restored);
        Assert.Equal(selectedPath, restored!.FilePath);
        Assert.Equal(600, model.VisibleCount);
        Assert.Contains(restored, model.VisibleItems);
    }

    [Fact]
    public void CancelledRefresh_LeavesPreviousCompletePageUntouched()
    {
        var model = new HistoryIncrementalCollection();
        List<HistoryItem> original = CreateItems(200);
        model.ReplaceAll(original, static _ => true, selectedPath: null);
        using var cts = new CancellationTokenSource();

        IEnumerable<HistoryItem> CancellingCandidate()
        {
            foreach ((HistoryItem item, int index) in CreateItems(5_000).Select((item, index) => (item, index)))
            {
                if (index == 25)
                    cts.Cancel();
                yield return item;
            }
        }

        Assert.Throws<OperationCanceledException>(() =>
            model.ReplaceAll(CancellingCandidate(), static _ => true, selectedPath: null, cts.Token));

        Assert.Equal(200, model.TotalCount);
        Assert.Equal(original[0].FilePath, model.VisibleItems[0].FilePath);
        Assert.False(model.HasMore);
    }

    [Fact]
    public void FilterAndDelete_PreserveGroupingAndBackfillTheVisiblePage()
    {
        var model = new HistoryIncrementalCollection();
        List<HistoryItem> items = CreateItems(500);
        model.ReplaceAll(items, static _ => true, selectedPath: null);

        HistoryItem? restored = model.ApplyFilter(item => item.IsImage, items[10].FilePath);

        Assert.NotNull(restored);
        Assert.Equal(250, model.FilteredCount);
        Assert.Equal(200, model.VisibleCount);
        Assert.All(model.VisibleItems, item => Assert.True(item.IsImage));
        Assert.All(model.VisibleItems, item => Assert.False(string.IsNullOrWhiteSpace(item.DayGroup)));

        HistoryItem removed = model.VisibleItems[20];
        model.Remove(removed);

        Assert.Equal(249, model.FilteredCount);
        Assert.Equal(200, model.VisibleCount);
        Assert.DoesNotContain(removed, model.VisibleItems);
    }

    private static List<HistoryItem> CreateItems(int count)
    {
        DateTime start = DateTime.Today.AddHours(12);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                string extension = index % 2 == 0 ? ".png" : ".mp4";
                string name = $"{start.AddMinutes(-index):yyyyMMdd-HHmmss-fff}{extension}";
                return new HistoryItem(Path.Combine("C:\\sanitized-history", name));
            })
            .ToList();
    }
}
