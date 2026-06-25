using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private readonly CollectionViewSource _itemsView = new();
    private CancellationTokenSource? _loadCts;
    private string _filter = "all";
    private HistoryItem? _previewItem;
    private FastQuickPreviewWindow? _preview;
    private bool _previewOpen;
    private Point _dragStart;
    private HistoryItem? _dragItem;
    private bool _dragging;
    private bool _loadedOnce;
    private bool _renderPrewarmed;
    private bool _suppressRefreshOnLoad;
    private bool _parkedVisible;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;

    public HistoryWindow(HistoryService history, SettingsService settings)
    {
        // Ensure the shared theme dictionary is available before the XAML (which references
        // theme brushes like WindowBgBrush) is parsed — don't rely on another window having
        // loaded it first. Idempotent; a no-op once App.xaml has merged it app-wide.
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _history = history;
        _settings = settings;

        // Grouped view over the filtered items: tiles are grouped under day headers
        // ("Today", "Yesterday", weekday, or "MMM d") using the parsed capture time.
        // Source order is already newest-first, so groups appear in that order too.
        _itemsView.Source = _items;
        _itemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItem.DayGroup)));
        ItemsList.ItemsSource = _itemsView.View;
        ItemsList.SelectionChanged += OnSelectionChanged;

        Loaded += (_, _) =>
        {
            _loadedOnce = true;
            StartWatcher();
            if (_suppressRefreshOnLoad) return;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => _ = RefreshOnShowAsync()));
        };
        IsVisibleChanged += (_, e) =>
        {
            // Keep the live watcher running only while the window is actually shown.
            if (e.NewValue is true) StartWatcher();
            else StopWatcher();
        };
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            StopWatcher();
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
        // The parked window was already IsVisible, so IsVisibleChanged won't fire on
        // restore — start the live watcher here.
        StartWatcher();
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
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                return;
            case Key.Enter:
                if (SelectedItem() is { } toOpen)
                {
                    OpenItem(toOpen);
                    e.Handled = true;
                }
                return;
            case Key.Delete:
                if (SelectedItem() is { } toDelete)
                {
                    DeleteItem(toDelete);
                    e.Handled = true;
                }
                return;
        }
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
        UpdateEmptyState();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Tunneling so the chips/scroll viewer never swallow Space.
        if (e.Key == Key.Space)
        {
            ToggleQuickPreview();
            e.Handled = true;
            return;
        }

        if (_previewOpen)
        {
            // While the quick-preview is open, Left/Right step to the adjacent item and
            // re-show the preview for it WITHOUT collapsing back to the grid.
            if (e.Key == Key.Left || e.Key == Key.Right)
            {
                StepPreview(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                return;
            }
            // Escape closes the preview first, leaving the History grid open.
            if (e.Key == Key.Escape)
            {
                ClosePreview();
                e.Handled = true;
                return;
            }
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
                // Remember selection by path so a live refresh doesn't yank it away;
                // the rebuilt list holds fresh HistoryItem instances.
                string? selectedPath = SelectedItem()?.FilePath;
                _allItems.Clear();
                _allItems.AddRange(newItems);
                ApplyFilter();
                RestoreSelection(selectedPath);
                // Give keyboard nav an anchor when nothing was selected yet.
                if (ItemsList.SelectedItem is null && _items.Count > 0)
                {
                    ItemsList.SelectedItem = _items[0];
                    _previewItem = _items[0];
                }
                // Park keyboard focus on the grid so arrow keys drive selection
                // immediately, without the user having to click a tile first.
                if (_items.Count > 0 && !ItemsList.IsKeyboardFocusWithin && IsActive)
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ItemsList.Focus()));
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

    // ---- Live auto-refresh ----

    /// <summary>Watches the history folder so captures taken elsewhere (or by WinShot
    /// while History is open) appear live. Events are debounced and marshalled to the
    /// UI thread; the manual Refresh button stays as a fallback.</summary>
    private void StartWatcher()
    {
        // Don't spin the watcher up during the invisible prewarm pass.
        if (_watcher is not null || !IsVisible || _suppressRefreshOnLoad) return;

        string dir = HistoryService.Dir;
        try
        {
            Directory.CreateDirectory(dir);
            _watcherDebounce ??= CreateWatcherDebounce();
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Created += OnHistoryFolderChanged;
            watcher.Deleted += OnHistoryFolderChanged;
            watcher.Renamed += OnHistoryFolderChanged;
            watcher.Changed += OnHistoryFolderChanged;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start history folder watcher", ex);
        }
    }

    private DispatcherTimer CreateWatcherDebounce()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsVisible) _ = ReloadAsync();
        };
        return timer;
    }

    private void OnHistoryFolderChanged(object sender, FileSystemEventArgs e)
    {
        // Raised on a threadpool thread; coalesce bursts on the UI thread.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _watcherDebounce?.Stop();
            _watcherDebounce?.Start();
        }));
    }

    private void StopWatcher()
    {
        _watcherDebounce?.Stop();
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnHistoryFolderChanged;
            _watcher.Deleted -= OnHistoryFolderChanged;
            _watcher.Renamed -= OnHistoryFolderChanged;
            _watcher.Changed -= OnHistoryFolderChanged;
            _watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop history folder watcher", ex);
        }
        finally
        {
            _watcher = null;
        }
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
        UpdateEmptyState();
    }

    /// <summary>Re-selects the item with the given path after the list is rebuilt,
    /// so keyboard focus and the preview anchor survive a refresh.</summary>
    private void RestoreSelection(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        HistoryItem? match = _items.FirstOrDefault(i =>
            string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            ItemsList.SelectedItem = match;
            _previewItem = match;
        }
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Spacebar: open the quick preview for the current item, or close it if
    /// it is already open (Quick Look toggle).</summary>
    private void ToggleQuickPreview()
    {
        if (_previewOpen)
        {
            ClosePreview();
            return;
        }

        HistoryItem? target = _previewItem ?? SelectedItem() ?? _items.FirstOrDefault();
        if (target is null) return;
        // Make sure selection follows the preview so arrow stepping has an anchor.
        SelectItem(target);
        OpenPreviewFor(target);
    }

    /// <summary>Left/Right while the preview is open: move selection to the adjacent
    /// item (within the flattened, grouped order) and re-show the preview for it.</summary>
    private void StepPreview(int direction)
    {
        if (_items.Count == 0) return;
        HistoryItem? current = _previewItem ?? SelectedItem();
        int index = current is null ? -1 : _items.IndexOf(current);
        int next = index < 0
            ? (direction > 0 ? 0 : _items.Count - 1)
            : index + direction;
        if (next < 0 || next >= _items.Count) return;

        HistoryItem target = _items[next];
        SelectItem(target);
        OpenPreviewFor(target);
    }

    private void OpenPreviewFor(HistoryItem target)
    {
        if (!File.Exists(target.FilePath)) return;

        _previewItem = target;
        _preview?.Close();
        var preview = new FastQuickPreviewWindow(target.FilePath);
        FastQuickPreviewWindow.TrackFirstShown(preview, "history quick preview");
        _preview = preview;
        _previewOpen = true;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_preview, preview))
            {
                _preview = null;
                _previewOpen = false;
            }
        };
        preview.Show();
        // Keep keyboard focus on History so Left/Right stepping continues to work.
        Activate();
    }

    private void ClosePreview()
    {
        _previewOpen = false;
        _preview?.Close();
        _preview = null;
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        if (GetItem(sender) is { } item)
            _previewItem = item;
    }

    // ---- Selection ----

    private HistoryItem? SelectedItem() => ItemsList.SelectedItem as HistoryItem;

    private void SelectItem(HistoryItem item)
    {
        if (ReferenceEquals(ItemsList.SelectedItem, item)) return;
        ItemsList.SelectedItem = item;
        ItemsList.ScrollIntoView(item);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection drives the spacebar preview target.
        if (SelectedItem() is { } item)
            _previewItem = item;
    }

    // ---- Tile actions ----

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        _previewItem = item;
        if (e.ClickCount == 2)
        {
            _dragItem = null;
            OpenItem(item);
            return;
        }
        // Arm a potential drag-out; the actual drag begins once the mouse moves
        // past the system threshold while the left button stays down.
        _dragStart = e.GetPosition(null);
        _dragItem = item;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _dragItem;
        if (!File.Exists(item.FilePath))
        {
            _dragItem = null;
            return;
        }

        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { item.FilePath });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Error($"Drag-out failed for {item.FilePath}", ex);
        }
        finally
        {
            _dragging = false;
            _dragItem = null;
        }
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

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
            DeleteItem(item);
    }

    private async void DeleteItem(HistoryItem item)
    {
        // Pick the neighbour to land selection on after the row disappears.
        int index = _items.IndexOf(item);
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
        UpdateEmptyState();

        // Keep keyboard navigation flowing: select the next (or previous) tile.
        if (_items.Count > 0 && index >= 0)
        {
            int next = Math.Min(index, _items.Count - 1);
            SelectItem(_items[next]);
        }
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
        CapturedAt = ParseCapturedAt(filePath);
        FileSize = TryGetFileSize(filePath);
        Caption = BuildCaption(CapturedAt, FileSize);
        DayGroup = BuildDayGroup(CapturedAt);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public bool IsImage { get; }
    public bool IsVideo { get; }
    public bool IsGif { get; }
    public string ExtensionLabel { get; }

    /// <summary>Capture time parsed from the file name ("yyyyMMdd-HHmmss-fff"),
    /// the same format HistoryService writes. Falls back to last-write time.</summary>
    public DateTime CapturedAt { get; }
    public long FileSize { get; }
    /// <summary>Friendly caption shown under the thumbnail, e.g. "Today 2:14 PM · 1.2 MB".</summary>
    public string Caption { get; }

    /// <summary>Day-header label for grouping: "TODAY", "YESTERDAY", a weekday name,
    /// or "MMM D" for older captures. Uppercased for the themed header.</summary>
    public string DayGroup { get; }

    private static string BuildDayGroup(DateTime captured)
    {
        DateTime today = DateTime.Today;
        if (captured.Date == today) return "TODAY";
        if (captured.Date == today.AddDays(-1)) return "YESTERDAY";
        // Within the last week, show the weekday name (e.g. "MONDAY").
        if (captured.Date > today.AddDays(-7))
            return captured.ToString("dddd",
                System.Globalization.CultureInfo.CurrentCulture).ToUpperInvariant();
        return captured.ToString("MMM d",
            System.Globalization.CultureInfo.CurrentCulture).ToUpperInvariant();
    }

    private static DateTime ParseCapturedAt(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length >= 19 &&
            DateTime.TryParseExact(name[..19], "yyyyMMdd-HHmmss-fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime ts))
        {
            return ts;
        }

        try { return File.GetLastWriteTime(filePath); }
        catch { return DateTime.Now; }
    }

    private static long TryGetFileSize(string filePath)
    {
        try { return new FileInfo(filePath).Length; }
        catch { return 0; }
    }

    private static string BuildCaption(DateTime captured, long bytes)
    {
        string when;
        DateTime today = DateTime.Today;
        if (captured.Date == today)
            when = $"Today {captured:h:mm tt}";
        else if (captured.Date == today.AddDays(-1))
            when = "Yesterday";
        else
            when = captured.ToString("MMM d");

        string size = FormatSize(bytes);
        return size.Length == 0 ? when : $"{when} · {size}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024d;
        return $"{mb:0.#} MB";
    }

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
