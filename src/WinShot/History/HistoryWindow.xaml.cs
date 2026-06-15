using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.History;

/// <summary>
/// Browsable grid of past captures. Thumbnails are decoded off the UI thread
/// with OnLoad caching so the underlying files stay unlocked. Filter chips
/// narrow the grid to images / video / GIF; Spacebar quick-previews the last
/// hovered or clicked tile.
/// </summary>
public partial class HistoryWindow : Window
{
    private static HistoryWindow? _instance;

    private readonly HistoryService _history;
    private readonly SettingsService _settings;
    private readonly List<HistoryItem> _allItems = new();
    private readonly ObservableCollection<HistoryItem> _items = new();
    private CancellationTokenSource? _loadCts;
    private string _filter = "all";
    private HistoryItem? _previewItem;
    private QuickPreviewWindow? _preview;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;

    public HistoryWindow(HistoryService history, SettingsService settings)
    {
        InitializeComponent();
        _history = history;
        _settings = settings;
        ItemsList.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            int retentionDays = _settings.Current.HistoryRetentionDays;
            if (retentionDays > 0)
            {
                try { await Task.Run(() => _history.PruneByAge(retentionDays)); }
                catch (Exception ex) { Log.Error("History age prune failed", ex); }
            }
            await ReloadAsync();
        };
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _preview?.Close();
        };
    }

    /// <summary>Opens the history window, or activates the instance that is already open.</summary>
    public static HistoryWindow Show(HistoryService history, SettingsService settings)
    {
        if (_instance is null)
        {
            _instance = new HistoryWindow(history, settings);
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
        return _instance;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Tunneling so the chips/scroll viewer never swallow Space.
        if (e.Key == Key.Space)
        {
            ShowQuickPreview();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var files = await Task.Run(_history.GetItems);
            if (cts.IsCancellationRequested) return;

            _allItems.Clear();
            foreach (string file in files)
                _allItems.Add(new HistoryItem(file));
            ApplyFilter();

            // Decode thumbnails one at a time on the pool; assignment happens back
            // on the UI thread after each await.
            foreach (var item in _allItems.Where(i => i.IsImage).ToList())
            {
                if (cts.IsCancellationRequested) return;
                var thumbnail = await Task.Run(() => TryLoadThumbnail(item.FilePath));
                if (cts.IsCancellationRequested) return;
                item.Thumbnail = thumbnail;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load history", ex);
        }
    }

    private static BitmapImage? TryLoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 360;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load thumbnail for {path}", ex);
            return null;
        }
    }

    // ---- Filter chips ----

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // The default-checked chip raises Checked while the XAML is still parsing.
        if (!IsInitialized) return;
        _filter = (sender as FrameworkElement)?.Name switch
        {
            "ChipImages" => "images",
            "ChipVideo" => "video",
            "ChipGif" => "gif",
            _ => "all",
        };
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _items.Clear();
        foreach (var item in _allItems.Where(MatchesFilter))
            _items.Add(item);
        UpdateCount();
    }

    private bool MatchesFilter(HistoryItem item) => _filter switch
    {
        "images" => item.IsImage,
        "video" => item.IsVideo,
        "gif" => item.IsGif,
        _ => true,
    };

    private void UpdateCount() =>
        CountText.Text = $"{_items.Count} item{(_items.Count == 1 ? "" : "s")} (limit {_settings.Current.HistoryLimit})";

    // ---- Quick preview (Spacebar) ----

    private void ShowQuickPreview()
    {
        HistoryItem? target = _previewItem ?? _items.FirstOrDefault();
        if (target is null || !File.Exists(target.FilePath)) return;

        _preview?.Close();
        var preview = new QuickPreviewWindow(target.FilePath) { Owner = this };
        _preview = preview;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_preview, preview)) _preview = null;
        };
        preview.Show();
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        if (GetItem(sender) is { } item)
            _previewItem = item;
    }

    // ---- Tile actions ----

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        _previewItem = item;
        if (e.ClickCount == 2)
            OpenItem(item);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { IsImage: true } item) return;
        try
        {
            using var stream = File.OpenRead(item.FilePath);
            using var bmp = new SD.Bitmap(stream);
            CaptureService.CopyToClipboard(bmp);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to copy history item {item.FilePath}", ex);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { IsImage: true } item)
            EditRequested?.Invoke(item.FilePath);
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
            PinRequested?.Invoke(item.FilePath);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
            OpenItem(item);
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to reveal {item.FilePath}", ex);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        _history.Delete(item.FilePath);
        _allItems.Remove(item);
        _items.Remove(item);
        if (ReferenceEquals(_previewItem, item)) _previewItem = null;
        UpdateCount();
    }

    private static void OpenItem(HistoryItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {item.FilePath}", ex);
        }
    }

    private static HistoryItem? GetItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as HistoryItem;
}

/// <summary>One history file shown as a tile. GIFs deliberately count as media,
/// not images, so they get the labelled tile and no Copy/Edit actions.</summary>
public sealed class HistoryItem : INotifyPropertyChanged
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".webm" };

    private ImageSource? _thumbnail;

    public HistoryItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ImageExtensions.Contains(ext);
        IsVideo = VideoExtensions.Contains(ext);
        IsGif = ext == ".gif";
        ExtensionLabel = ext.TrimStart('.').ToUpperInvariant();
    }

    public string FilePath { get; }
    public string FileName { get; }
    public bool IsImage { get; }
    public bool IsVideo { get; }
    public bool IsGif { get; }
    public string ExtensionLabel { get; }

    public Visibility ImageOnlyVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaOnlyVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
