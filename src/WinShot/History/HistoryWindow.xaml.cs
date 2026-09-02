using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using WF = System.Windows.Forms;

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
    private readonly HistoryIncrementalCollection _paging = new();
    private ObservableCollection<HistoryItem> _items => _paging.VisibleItems;
    private CancellationTokenSource? _loadCts;
    private string _filter = "all";
    private string _sort = "date";
    private bool _sortDescending = true;
    private bool _selectionMode;
    private HistoryItem? _selectionAnchor;
    private HistoryItem? _previewItem;
    private FastQuickPreviewWindow? _preview;
    private bool _previewOpen;
    private Point _dragStart;
    private HistoryItem? _dragItem;
    private bool _dragging;
    private bool _loadedOnce;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;
    public event Action<string>? RestoreRequested;
    public event Action<IReadOnlyList<string>>? MultiPinRequested;

    public HistoryWindow(HistoryService history, SettingsService settings)
    {
        // Ensure the shared theme dictionary is available before the XAML (which references
        // theme brushes like WindowBgBrush) is parsed — don't rely on another window having
        // loaded it first. Idempotent; a no-op once App.xaml has merged it app-wide.
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _history = history;
        _settings = settings;

        ItemsList.ItemsSource = _items;
        ItemsList.SelectionChanged += OnSelectionChanged;

        Loaded += (_, _) =>
        {
            _loadedOnce = true;
            StartWatcher();
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

        if (!instance.IsVisible)
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

    private static void CreateInstance(HistoryService history, SettingsService settings)
    {
        _instance = new HistoryWindow(history, settings);
        _instance.Closed += (_, _) => _instance = null;
    }

    private void CenterOnWorkArea()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        double dpiScale = GetCursorMonitorDpiScale();
        SD.Rectangle placement = HistoryWindowPlacement.CenterInWorkArea(
            area,
            new Size(Width, Height),
            dpiScale);
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!SetWindowPos(
                hwnd,
                IntPtr.Zero,
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                SwpNoZOrder | SwpNoActivate))
        {
            // SetWindowPos should be available on every supported Windows version.
            // Keep a safe primary-monitor fallback if an unusual shell rejects it.
            var fallback = SystemParameters.WorkArea;
            Left = fallback.Left + (fallback.Width - Width) / 2;
            Top = fallback.Top + (fallback.Height - Height) / 2;
        }
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
                if (_selectionMode && ItemsList.SelectedItems.Count > 0)
                {
                    _ = DeleteSelectedItems();
                    e.Handled = true;
                }
                else if (SelectedItem() is { } toDelete)
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
        ApplyParkedWindowStyle(parked: false);
        base.OnClosing(e);
    }

    private void ClearLoadedItems()
    {
        foreach (var item in _paging.AllItems)
            item.Thumbnail = null;
        _paging.Clear();
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

        // Grow the bounded collection before WPF tries to move past the final visible
        // card. This keeps keyboard navigation continuous without realizing the full
        // retained library at startup.
        if (!_previewOpen && _paging.HasMore &&
            (e.Key == Key.Right || e.Key == Key.Down || e.Key == Key.PageDown) &&
            ItemsList.SelectedIndex >= _items.Count - 1)
        {
            IReadOnlyList<HistoryItem> added = _paging.LoadNextPage();
            UpdateCollectionState();
            _ = LoadThumbnailsSafelyAsync(added, _loadCts?.Token ?? CancellationToken.None);
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

            cts.Token.ThrowIfCancellationRequested();
            var newItems = files.Select(file => new HistoryItem(file)).ToList();
            IReadOnlyList<HistoryItem> visibleItems = Array.Empty<HistoryItem>();
            await Dispatcher.InvokeAsync(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                // Remember selection by path so a live refresh doesn't yank it away;
                // the rebuilt list holds fresh HistoryItem instances.
                string? selectedPath = SelectedItem()?.FilePath;
                var previousItems = _paging.AllItems.ToList();
                HistoryItem? restored = _paging.ReplaceAll(
                    newItems,
                    MatchesFilter,
                    selectedPath,
                    cts.Token,
                    CurrentComparer());
                foreach (HistoryItem previous in previousItems)
                    previous.Thumbnail = null;
                UpdateCollectionState();
                if (restored is not null)
                {
                    ItemsList.SelectedItem = restored;
                    _previewItem = restored;
                }
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

                visibleItems = _items.ToList();
            });

            // Decode only the bounded visible page. A 5,000-file library therefore
            // starts with at most 200 thumbnail decodes and card visuals.
            await LoadThumbnailsAsync(visibleItems, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer refresh owns the UI; leave the last complete page intact.
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
        if (_watcher is not null || !IsVisible) return;

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

    private async Task LoadThumbnailsAsync(
        IEnumerable<HistoryItem> items,
        CancellationToken cancellationToken)
    {
        foreach (HistoryItem item in items.Where(item => (item.IsImage || item.IsGif) && item.Thumbnail is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapImage? thumbnail = await Task.Run(
                () => TryLoadThumbnail(item.FilePath),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail);
        }
    }

    private async Task LoadThumbnailsSafelyAsync(
        IEnumerable<HistoryItem> items,
        CancellationToken cancellationToken)
    {
        try
        {
            await LoadThumbnailsAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load visible history thumbnails", ex);
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
            "ChipScrolling" => "scrolling",
            "ChipVideo" => "video",
            "ChipGif" => "gif",
            _ => "all",
        };
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string? selectedPath = SelectedItem()?.FilePath;
        HistoryItem? restored = _paging.ApplyFilter(MatchesFilter, selectedPath, CurrentComparer());
        ItemsList.SelectedItem = restored ?? _items.FirstOrDefault();
        _previewItem = ItemsList.SelectedItem as HistoryItem;
        UpdateCollectionState();
        _ = LoadThumbnailsSafelyAsync(_items.ToList(), _loadCts?.Token ?? CancellationToken.None);
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool MatchesFilter(HistoryItem item) => _filter switch
    {
        "images" => item.IsImage,
        "scrolling" => item.IsScrollingCapture,
        "video" => item.IsVideo,
        "gif" => item.IsGif,
        _ => true,
    };

    private IComparer<HistoryItem> CurrentComparer() => Comparer<HistoryItem>.Create((left, right) =>
    {
        int result = _sort switch
        {
            "name" => StringComparer.CurrentCultureIgnoreCase.Compare(left.FileName, right.FileName),
            "size" => left.FileSize.CompareTo(right.FileSize),
            _ => left.CapturedAt.CompareTo(right.CapturedAt),
        };
        if (result == 0)
            result = StringComparer.OrdinalIgnoreCase.Compare(left.FilePath, right.FilePath);
        return _sortDescending ? -result : result;
    });

    private void OnSortFieldMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu"), PlacementTarget = SortFieldButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach ((string label, string value) in new[] { ("Date", "date"), ("Name", "name"), ("Size", "size") })
        {
            var item = new MenuItem { Header = label, Tag = value, IsCheckable = true, IsChecked = _sort == value, Style = (Style)FindResource("DarkMenuItem") };
            item.Click += (_, _) =>
            {
                _sort = value;
                SortFieldText.Text = label;
                ApplyFilter();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnToggleSortDirection(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        SortDirectionGlyph.Text = _sortDescending ? "\uE74B" : "\uE74A";
        SortDirectionButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            _sortDescending ? "Sort descending" : "Sort ascending");
        ApplyFilter();
    }

    private void UpdateCount()
    {
        string count = _paging.HasMore
            ? $"{_paging.VisibleCount:N0} of {_paging.FilteredCount:N0} items shown"
            : $"{_paging.FilteredCount:N0} item{(_paging.FilteredCount == 1 ? "" : "s")}";
        CountText.Text = count;
        LoadMoreButton.Visibility = _paging.HasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCollectionState()
    {
        UpdateCount();
        UpdateEmptyState();
        UpdateSelectionState();
    }

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
        if (direction > 0 && next >= _items.Count && _paging.HasMore)
        {
            IReadOnlyList<HistoryItem> added = _paging.LoadNextPage();
            UpdateCollectionState();
            _ = LoadThumbnailsSafelyAsync(added, _loadCts?.Token ?? CancellationToken.None);
        }
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
        PerfLog.TrackFirstShown(preview, "history quick preview");
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
        UpdateSelectionState();
    }

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(!_selectionMode);
    }

    private void SetSelectionMode(bool enabled)
    {
        if (_selectionMode == enabled)
            return;

        _selectionMode = enabled;
        ItemsList.Tag = _selectionMode;
        ItemsList.SelectionMode = _selectionMode ? SelectionMode.Extended : SelectionMode.Single;
        NormalToolbar.Visibility = _selectionMode ? Visibility.Collapsed : Visibility.Visible;
        SelectionToolbar.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_selectionMode)
        {
            HistoryItem? anchor = ItemsList.SelectedItems.Cast<HistoryItem>().FirstOrDefault();
            ItemsList.UnselectAll();
            if (anchor is not null)
                ItemsList.SelectedItem = anchor;
        }
        else
        {
            ItemsList.UnselectAll();
        }
        _selectionAnchor = null;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        int count = _selectionMode ? ItemsList.SelectedItems.Count : 0;
        long bytes = _selectionMode
            ? ItemsList.SelectedItems.Cast<HistoryItem>().Sum(item => item.FileSize)
            : 0;
        SelectionCountText.Text = $"{count:N0} selected";
        SelectionSummaryText.Text = count == 0 ? string.Empty : $"{count:N0} selected    {HistoryItem.FormatFileSize(bytes)}";
        bool hasSelection = count > 0;
        CopyToButton.IsEnabled = hasSelection;
        ShareButton.IsEnabled = hasSelection;
        DeleteSelectedButton.IsEnabled = hasSelection;
        PinSelectedButton.IsEnabled = hasSelection && ItemsList.SelectedItems.Cast<HistoryItem>().Any(item => item.CanPin);
    }

    private void OnShowAllFromSelection(object sender, RoutedEventArgs e)
    {
        if (!_selectionMode) return;
        if (ItemsList.SelectedItems.Count == _items.Count && _items.Count > 0)
            ItemsList.UnselectAll();
        else
            ItemsList.SelectAll();
        _selectionAnchor = _items.FirstOrDefault();
        UpdateSelectionState();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (!_selectionMode) return;
        ItemsList.SelectAll();
        _selectionAnchor = _items.FirstOrDefault();
        UpdateSelectionState();
    }

    private IReadOnlyList<HistoryItem> SelectedItems() =>
        ItemsList.SelectedItems.Cast<HistoryItem>().Where(item => File.Exists(item.FilePath)).ToList();

    private void OnCopySelected(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<HistoryItem> selected = SelectedItems();
        if (selected.Count == 0) return;
        try
        {
            var files = new StringCollection();
            files.AddRange(selected.Select(item => item.FilePath).ToArray());
            Clipboard.SetFileDropList(files);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to copy selected history files", ex);
            ThemedMessageDialog.Show(this, "Copy failed", "WinShot could not copy the selected files to the clipboard.");
        }
    }

    private void OnPinSelected(object sender, RoutedEventArgs e)
    {
        var paths = SelectedItems().Where(item => item.CanPin).Select(item => item.FilePath).ToList();
        if (paths.Count > 0)
            MultiPinRequested?.Invoke(paths);
    }

    private void OnShareSelected(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> paths = SelectedItems().Select(item => item.FilePath).ToList();
        if (paths.Count == 0) return;

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        if (HistoryShareService.IsBlipInstalled())
        {
            var blip = new MenuItem { Header = "Blip", Style = (Style)FindResource("DarkMenuItem") };
            blip.Click += (_, _) =>
            {
                switch (HistoryShareService.ShareWithBlip(paths))
                {
                    case BlipShareResult.NeedsRestart:
                        ThemedMessageDialog.Show(this, "Blip can't receive shares",
                            "Blip is running but is no longer accepting handoffs. Restart Blip and try again.");
                        break;
                    case BlipShareResult.Failed:
                        ThemedMessageDialog.Show(this, "Share failed", "WinShot could not hand these files to Blip.");
                        break;
                }
            };
            menu.Items.Add(blip);
        }

        var windowsShare = new MenuItem
        {
            Header = "Windows Share...",
            Style = (Style)FindResource("DarkMenuItem"),
            IsEnabled = HistoryShareService.IsWindowsShareSupported(),
        };
        windowsShare.Click += (_, _) => HistoryShareService.ShowWindowsShare(this, paths, ex =>
        {
            Log.Error("Windows Share failed", ex);
            Dispatcher.BeginInvoke(new Action(() => ThemedMessageDialog.Show(this, "Share failed", "WinShot could not prepare these files for sharing.")));
        });
        menu.Items.Add(windowsShare);
        menu.PlacementTarget = ShareButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void OnDeleteSelected(object sender, RoutedEventArgs e) => await DeleteSelectedItems();

    private async Task DeleteSelectedItems()
    {
        IReadOnlyList<HistoryItem> selected = SelectedItems();
        if (selected.Count == 0) return;
        if (!ThemedMessageDialog.Confirm(this, "Delete selected items",
                $"Delete {selected.Count:N0} selected history item{(selected.Count == 1 ? string.Empty : "s")}?\n\nThis removes only the local History copies.",
                "Delete", destructive: true))
            return;

        var failed = new List<string>();
        foreach (HistoryItem item in selected)
        {
            HistoryDeleteResult result = await Task.Run(() => _history.Delete(item.FilePath));
            if (result.Succeeded)
                _paging.Remove(item);
            else
                failed.Add(item.FileName);
        }
        ItemsList.UnselectAll();
        if (HistorySelectionLogic.ShouldExitSelectionAfterDelete(_paging.TotalCount))
            SetSelectionMode(false);
        else
            UpdateCollectionState();
        _ = LoadThumbnailsSafelyAsync(_items.ToList(), _loadCts?.Token ?? CancellationToken.None);
        if (failed.Count > 0)
            ThemedMessageDialog.Show(this, "Delete incomplete", $"WinShot could not delete {failed.Count:N0} selected item{(failed.Count == 1 ? string.Empty : "s")}.");
    }

    // ---- Tile actions ----

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnLoadMore(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<HistoryItem> added = _paging.LoadNextPage();
        UpdateCollectionState();
        await LoadThumbnailsSafelyAsync(added, _loadCts?.Token ?? CancellationToken.None);
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        _previewItem = item;
        if (_selectionMode)
        {
            _dragItem = null;
            e.Handled = true;
            if (e.ClickCount > 1)
                return;

            ApplySelectionClick(item, Keyboard.Modifiers);
            return;
        }
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

    private void ApplySelectionClick(HistoryItem item, ModifierKeys modifiers)
    {
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        int targetIndex = _items.IndexOf(item);
        int anchorIndex = _selectionAnchor is null ? -1 : _items.IndexOf(_selectionAnchor);
        if (targetIndex < 0)
            return;

        HistorySelectionChange change = HistorySelectionLogic.ResolveClick(
            _items.Count,
            anchorIndex,
            targetIndex,
            shift,
            ItemsList.SelectedItems.Contains(item));
        for (int index = change.Start; index <= change.End; index++)
        {
            HistoryItem rangeItem = _items[index];
            if (change.Select)
            {
                if (!ItemsList.SelectedItems.Contains(rangeItem))
                    ItemsList.SelectedItems.Add(rangeItem);
            }
            else
            {
                ItemsList.SelectedItems.Remove(rangeItem);
            }
        }
        _selectionAnchor = _items[change.Anchor];

        _previewItem = item;
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            container.Focus();
        UpdateSelectionState();
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
            var bmp = await Task.Run(() => HistoryImageLoader.LoadDetachedBitmap(item.FilePath));
            await CaptureService.CopyToClipboardAsync(bmp, takeOwnership: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to copy history item {item.FilePath}", ex);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        // Videos are editable too — App.EditFromHistory routes them to the video editor.
        if (GetItem(sender) is { IsEditable: true } item)
            EditRequested?.Invoke(item.FilePath);
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { CanPin: true } item)
            PinRequested?.Invoke(item.FilePath);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { IsImage: true } item)
            RestoreRequested?.Invoke(item.FilePath);
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
            HistoryDeleteResult result = await Task.Run(() => _history.Delete(item.FilePath));
            if (!result.Succeeded)
            {
                string detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Windows could not remove this history item from disk."
                    : result.ErrorMessage;
                ThemedMessageDialog.Show(this, "Delete failed",
                    $"WinShot could not delete this history item.\n\n{detail}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete history item {item.FilePath}", ex);
            return;
        }
        _paging.Remove(item);
        if (ReferenceEquals(_previewItem, item)) _previewItem = null;
        UpdateCollectionState();
        _ = LoadThumbnailsSafelyAsync(_items.ToList(), _loadCts?.Token ?? CancellationToken.None);

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
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static double GetCursorMonitorDpiScale()
    {
        try
        {
            SD.Point cursor = WF.Cursor.Position;
            IntPtr monitor = MonitorFromPoint(new NativePoint(cursor.X, cursor.Y), MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out _) == 0)
                return Math.Max(1, dpiX / 96d);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read cursor monitor DPI for History placement", ex);
        }

        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
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
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ImageExtensions.Contains(ext);
        IsVideo = VideoExtensions.Contains(ext);
        IsGif = ext == ".gif";
        IsScrollingCapture = IsImage && Path.GetFileNameWithoutExtension(filePath).EndsWith("-scroll", StringComparison.OrdinalIgnoreCase);
        ExtensionLabel = ext.TrimStart('.').ToUpperInvariant();
        FileName = Path.GetFileName(filePath);
        CapturedAt = ParseCapturedAt(filePath);
        FileSize = TryGetFileSize(filePath);
        Caption = BuildCaption(CapturedAt, FileSize);
        RelativeTime = BuildRelativeTime(CapturedAt);
        DayGroup = BuildDayGroup(CapturedAt);
        string sizeText = FormatFileSize(FileSize);
        MetadataText = sizeText.Length == 0 ? ExtensionLabel : $"{sizeText} · {ExtensionLabel}";
    }

    public string FilePath { get; }
    public bool IsImage { get; }
    public bool IsVideo { get; }
    public bool IsGif { get; }
    public bool IsScrollingCapture { get; }
    public bool CanPin => IsImage;
    public string ExtensionLabel { get; }
    public string FileName { get; }
    public string MetadataText { get; }

    /// <summary>Capture time parsed from the file name ("yyyyMMdd-HHmmss-fff"),
    /// the same format HistoryService writes. Falls back to last-write time.</summary>
    public DateTime CapturedAt { get; }
    public long FileSize { get; }
    /// <summary>Friendly caption (filename context), e.g. "Today 2:14 PM · 1.2 MB".
    /// Shown as the tooltip on the card caption.</summary>
    public string Caption { get; }

    /// <summary>CleanShot-style relative-time caption shown under the thumbnail,
    /// e.g. "1 minute ago", "3 minutes ago", "Yesterday".</summary>
    public string RelativeTime { get; }

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

    /// <summary>Builds a CleanShot-style relative-time string from the capture time:
    /// "Just now", "1 minute ago", "3 minutes ago", "2 hours ago", "Yesterday",
    /// or an absolute "MMM d" date for older captures.</summary>
    private static string BuildRelativeTime(DateTime captured)
    {
        TimeSpan delta = DateTime.Now - captured;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45) return "Just now";
        if (delta.TotalMinutes < 60)
        {
            int m = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return m == 1 ? "1 minute ago" : $"{m} minutes ago";
        }
        if (delta.TotalHours < 24)
        {
            int h = (int)delta.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }

        DateTime today = DateTime.Today;
        if (captured.Date == today.AddDays(-1)) return "Yesterday";
        if (delta.TotalDays < 7)
        {
            int d = (int)delta.TotalDays;
            return d == 1 ? "1 day ago" : $"{d} days ago";
        }
        return captured.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024d;
        return $"{mb:0.#} MB";
    }

    private static string FormatSize(long bytes) => FormatFileSize(bytes);

    public Visibility ImageOnlyVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Images open the annotation editor; MP4s open the video editor.</summary>
    public bool IsEditable => IsImage || IsVideo;

    public Visibility EditableVisibility => IsEditable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaOnlyVisibility => IsImage ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PinVisibility => CanPin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => IsImage || IsGif ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaFallbackVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingThumbnailVisibility => (IsImage || IsGif) && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ScrollingVisibility => IsScrollingCapture ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GifBadgeVisibility => IsGif ? Visibility.Visible : Visibility.Collapsed;
    public string AccessibleName => $"{(IsScrollingCapture ? "Scrolling screenshot" : IsImage ? "Screenshot" : ExtensionLabel)} captured {Caption}";
    public string AccessibleHelpText =>
        CanPin
            ? "Press Enter to open, Space to preview, or Tab for Copy, Edit, Pin, and Delete actions."
            : "Press Enter to open, Space to preview, or Tab for Delete. Pin is available only for still images.";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingThumbnailVisibility)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
