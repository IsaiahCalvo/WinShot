using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
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
    private FastQuickPreviewWindow? _preview;
    private bool _loadedOnce;
    private bool _renderPrewarmed;
    private bool _suppressRefreshOnLoad;
    private bool _parkedVisible;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;

    public HistoryWindow(HistoryService history, SettingsService settings)
    {
        InitializeComponent();
        _history = history;
        _settings = settings;
        ItemsList.ItemsSource = _items;
        Loaded += (_, _) =>
        {
            _loadedOnce = true;
            if (_suppressRefreshOnLoad) return;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _ = RefreshOnShowAsync()));
        };
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _preview?.Close();
            ClearLoadedItems();
            MemoryCleanup.Request();
        };
        DarkTitleBar.Apply(this);
    }

    /// <summary>Opens the history window, or activates the instance that is already open.</summary>
    public static HistoryWindow Show(HistoryService history, SettingsService settings)
    {
        var total = Stopwatch.StartNew();
        long createMs = 0;
        long centerMs = 0;
        long showMs = 0;
        long activateMs = 0;
        bool wasVisible = false;

        if (_instance is null)
        {
            var create = Stopwatch.StartNew();
            CreateInstance(history, settings);
            createMs = create.ElapsedMilliseconds;
        }

        var instance = _instance ?? throw new InvalidOperationException("History window was not created.");
        bool deferActivate = false;
        wasVisible = instance.IsVisible;

        if (instance._parkedVisible)
        {
            var center = Stopwatch.StartNew();
            instance.RestoreParkedWindow();
            centerMs = center.ElapsedMilliseconds;
            if (instance._loadedOnce)
                instance.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => _ = instance.RefreshOnShowAsync()));
            deferActivate = true;
        }
        else if (!instance.IsVisible)
        {
            var center = Stopwatch.StartNew();
            instance.ShowInTaskbar = true;
            instance.CenterOnWorkArea();
            centerMs = center.ElapsedMilliseconds;
            var show = Stopwatch.StartNew();
            instance.Show();
            showMs = show.ElapsedMilliseconds;
            if (instance._loadedOnce)
                instance.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => _ = instance.RefreshOnShowAsync()));
            deferActivate = true;
        }
        else if (instance.WindowState == WindowState.Minimized)
        {
            instance.WindowState = WindowState.Normal;
        }

        if (instance.Left < -10000 || instance.Top < -10000)
            instance.CenterOnWorkArea();

        if (deferActivate)
        {
            instance.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => instance.Activate()));
        }
        else
        {
            var activate = Stopwatch.StartNew();
            instance.Activate();
            activateMs = activate.ElapsedMilliseconds;
        }
        if (total.ElapsedMilliseconds > 50)
        {
            Log.Info(
                "Perf history window breakdown: " +
                $"create={createMs} center={centerMs} show={showMs} " +
                $"activate={activateMs} visible={wasVisible} total={total.ElapsedMilliseconds} ms");
        }
        return instance;
    }

    public static void Prewarm(HistoryService history, SettingsService settings)
    {
        if (_instance is null)
            CreateInstance(history, settings);
        _instance?.PrewarmRender();
    }

    private static void CreateInstance(HistoryService history, SettingsService settings)
    {
        _instance = new HistoryWindow(history, settings);
        _instance.Closed += (_, _) => _instance = null;
    }

    private void PrewarmRender()
    {
        if (_renderPrewarmed || IsVisible) return;
        _renderPrewarmed = true;

        _suppressRefreshOnLoad = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Opacity = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        CenterOnWorkArea();

        Show();
        FlushPrewarmRender();
        ParkWindow();
        _suppressRefreshOnLoad = false;
    }

    private void CenterOnWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private bool IsMostlyWithinWorkArea()
    {
        var area = SystemParameters.WorkArea;
        return Left < area.Right - 120 &&
               Left + Width > area.Left + 120 &&
               Top < area.Bottom - 80 &&
               Top + Height > area.Top + 80;
    }

    private void RestoreParkedWindow()
    {
        if (!IsMostlyWithinWorkArea())
            CenterOnWorkArea();
        ShowInTaskbar = true;
        Opacity = 1;
        ApplyParkedWindowStyle(parked: false);
        _parkedVisible = false;
    }

    private void ParkWindow()
    {
        Opacity = 0;
        ApplyParkedWindowStyle(parked: true);
        _parkedVisible = true;
    }

    private void ApplyParkedWindowStyle(bool parked)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GwlExStyle);
        int parkedFlags = WsExTransparent;
        int updated = parked ? style | parkedFlags : style & ~parkedFlags;
        if (updated == style)
            return;

        SetWindowLong(hwnd, GwlExStyle, updated);
    }

    private void FlushPrewarmRender()
    {
        var frame = new DispatcherFrame();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _parkedVisible = false;
        ApplyParkedWindowStyle(parked: false);
        base.OnClosing(e);
    }

    private void ClearLoadedItems()
    {
        foreach (var item in _allItems)
            item.Thumbnail = null;
        _allItems.Clear();
        _items.Clear();
        UpdateCount();
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
            var files = await Task.Run(_history.GetItems).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;

            var newItems = files.Select(file => new HistoryItem(file)).ToList();
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                _allItems.Clear();
                _allItems.AddRange(newItems);
                ApplyFilter();
            });

            // Decode thumbnails one at a time on the pool; assignment happens back
            // on the UI thread after each await.
            foreach (var item in newItems.Where(i => i.IsImage))
            {
                if (cts.IsCancellationRequested) return;
                var thumbnail = await Task.Run(() => TryLoadThumbnail(item.FilePath)).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested)
                        item.Thumbnail = thumbnail;
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load history", ex);
        }
    }

    private async Task RefreshOnShowAsync()
    {
        int retentionDays = _settings.Current.HistoryRetentionDays;
        if (retentionDays > 0)
        {
            try { await Task.Run(() => _history.PruneByAge(retentionDays)).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("History age prune failed", ex); }
        }
        await ReloadAsync().ConfigureAwait(false);
    }

    private static BitmapImage? TryLoadThumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 180;
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
        var preview = new FastQuickPreviewWindow(target.FilePath);
        FastQuickPreviewWindow.TrackFirstShown(preview, "history quick preview");
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

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { IsImage: true } item) return;
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var stream = File.OpenRead(item.FilePath);
                return new SD.Bitmap(stream);
            });
            await CaptureService.CopyToClipboardAsync(bmp, takeOwnership: true);
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

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        try
        {
            await Task.Run(() => _history.Delete(item.FilePath));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete history item {item.FilePath}", ex);
            return;
        }
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

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
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
