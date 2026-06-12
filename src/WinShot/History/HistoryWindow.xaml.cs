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
/// with OnLoad caching so the underlying files stay unlocked.
/// </summary>
public partial class HistoryWindow : Window
{
    private static HistoryWindow? _instance;

    private readonly HistoryService _history;
    private readonly SettingsService _settings;
    private readonly ObservableCollection<HistoryItem> _items = new();
    private CancellationTokenSource? _loadCts;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;

    public HistoryWindow(HistoryService history, SettingsService settings)
    {
        InitializeComponent();
        _history = history;
        _settings = settings;
        ItemsList.ItemsSource = _items;
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => _loadCts?.Cancel();
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

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var files = await Task.Run(_history.GetItems);
            if (cts.IsCancellationRequested) return;

            _items.Clear();
            foreach (string file in files)
                _items.Add(new HistoryItem(file));
            UpdateCount();

            // Decode thumbnails one at a time on the pool; assignment happens back
            // on the UI thread after each await.
            foreach (var item in _items.Where(i => i.IsImage).ToList())
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

    private void UpdateCount() =>
        CountText.Text = $"{_items.Count} item{(_items.Count == 1 ? "" : "s")} (limit {_settings.Current.HistoryLimit})";

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && GetItem(sender) is { } item)
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
        _items.Remove(item);
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
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    private ImageSource? _thumbnail;

    public HistoryItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ImageExtensions.Contains(ext);
        ExtensionLabel = ext.TrimStart('.').ToUpperInvariant();
    }

    public string FilePath { get; }
    public string FileName { get; }
    public bool IsImage { get; }
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
