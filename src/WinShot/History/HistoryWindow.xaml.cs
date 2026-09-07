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
using WinShot.Overlay;
using WinShot.Recording;
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
    private HistoryItem? _pendingSelectionClick;
    private bool _dragging;
    private bool _loadedOnce;
    private DragPreview? _dragPreview;
    private string? _loadError;
    private IDisposable? _windowFade;
    private bool _dismissing;
    private bool _closed;
    private IDisposable? _emptyStateFade;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;
    public event Action<string>? RestoreRequested;
    public event Action<IReadOnlyList<string>>? MultiPinRequested;

    /// <summary>Raised by the empty state's "New capture" button — the answer to a grid with
    /// nothing in it is usually to take one.</summary>
    public event Action? NewCaptureRequested;

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
            // Before anything else: emptying the grid on the way out must not start the
            // empty-state fade, which would outlive the window and hold the animation clock.
            _closed = true;
            _loadCts?.Cancel();
            StopWatcher();
            _preview?.Close();
            _windowFade?.Dispose();
            _windowFade = null;
            _emptyStateFade?.Dispose();
            _emptyStateFade = null;
            _dragPreview?.Dispose();
            _dragPreview = null;
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
            instance.FadeIn();
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

    /// <summary>
    /// The window arriving on screen. A grid of a hundred thumbnails appearing between two
    /// frames reads as a flash; over a fifth of a second it reads as opening.
    /// </summary>
    private void FadeIn()
    {
        _windowFade?.Dispose();
        Opacity = 0;
        bool finished = false;
        IDisposable handle = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseOutCubic,
            progress => Opacity = progress,
            () =>
            {
                finished = true;
                _windowFade = null;
            });
        _windowFade = finished ? null : handle;
    }

    /// <summary>
    /// Escape: fade out, then close, so dismissing is the entrance played backwards.
    ///
    /// ponytail: only this path fades. Doing it from OnClosing means cancelling the close and
    /// finishing it from a timer, and a Close() whose message loop never runs again would leave
    /// the window alive — which is exactly the shape that takes a WPF process down on the way
    /// out. The title-bar X stays instant, the way every other window on Windows closes.
    /// </summary>
    private void Dismiss()
    {
        if (_dismissing)
            return;

        _dismissing = true;
        _windowFade?.Dispose();
        _windowFade = Motion.Run(
            Motion.SwapMs,
            Motion.EaseInCubic,
            progress => Opacity = 1d - progress,
            () =>
            {
                _windowFade = null;
                Close();
            });
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
        if (HandleKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>The grid's key map, split from <see cref="OnKeyDown"/> so it can be driven
    /// with explicit modifiers instead of the machine's real keyboard state.</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Escape:
                Dismiss();
                return true;
            case Key.Enter:
                if (SelectedItem() is { } toActivate)
                {
                    ActivateItem(toActivate);
                    return true;
                }
                return false;
            case Key.Delete:
                if (_selectionMode && ItemsList.SelectedItems.Count > 0)
                {
                    _ = DeleteSelectedItems();
                    return true;
                }
                if (SelectedItem() is { } toDelete)
                {
                    DeleteItem(toDelete);
                    return true;
                }
                return false;
            case Key.C when modifiers == ModifierKeys.Control:
                // Selection mode is the batch gesture and already puts a file list on the
                // clipboard; outside it, Ctrl+C copies the picture itself so it can be pasted
                // straight into a chat or a document.
                if (_selectionMode && ItemsList.SelectedItems.Count > 0)
                {
                    OnCopySelected(this, new RoutedEventArgs());
                    return true;
                }
                if ((SelectedItem() ?? _previewItem) is { IsImage: true } toCopy)
                {
                    _ = CopyImageAsync(toCopy);
                    return true;
                }
                return false;
        }
        return false;
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
                _loadError = null;
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
            // A save folder on a disconnected drive is not "no captures yet" — say which it is,
            // and leave a way to try again once the drive is back.
            Log.Error("Failed to load history", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                _loadError = FriendlyError.For(ex);
                UpdateEmptyState();
            });
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

    /// <summary>How many thumbnails decode at once. Enough to hide the latency, few enough
    /// that a 200-card page does not take the whole thread pool away from the capture flow.</summary>
    internal static readonly int ThumbnailDecodeParallelism =
        Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

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
        var pending = items
            .Where(item => (item.IsImage || item.IsGif) && item.Thumbnail is null)
            .ToList();

        // A page is 200 files; decoding them one after another is why the grid used to fill in
        // visibly, top to bottom, over a static placeholder. They decode together instead, with
        // a cap so a big page cannot swamp the thread pool the rest of the app shares.
        using var decoding = new SemaphoreSlim(ThumbnailDecodeParallelism);
        await Task.WhenAll(pending.Select(async item =>
        {
            await decoding.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BitmapImage? thumbnail = await Task.Run(
                    () => TryLoadThumbnail(item.FilePath),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail);
            }
            finally
            {
                decoding.Release();
            }
        })).ConfigureAwait(false);

        await LoadVideoThumbnailsAsync(items, cancellationToken).ConfigureAwait(false);
        await LoadSourceAppIconsAsync(items, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recordings get a real first frame and their length, the same way the completion card
    /// already does. Decoding a video is far more expensive than a PNG, so it runs after the
    /// still images — a grid of screenshots must not wait behind one MP4.
    /// </summary>
    private async Task LoadVideoThumbnailsAsync(
        IEnumerable<HistoryItem> items,
        CancellationToken cancellationToken)
    {
        foreach (HistoryItem item in items.Where(item => item.IsVideo && item.Thumbnail is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.FilePath)) continue;

            (BitmapSource? frame, TimeSpan duration) = await LoadVideoFrameAsync(item.FilePath).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                item.Duration = duration;
                // A frame we could not decode leaves the "MP4" card up rather than blanking
                // the tile; the bar underneath still carries the size.
                if (frame is not null) item.Thumbnail = frame;
            });
        }
    }

    private static async Task<(BitmapSource? Frame, TimeSpan Duration)> LoadVideoFrameAsync(string path)
    {
        try
        {
            (SD.Bitmap image, TimeSpan duration) =
                await VideoThumbnail.CreateWithDurationAsync(path).ConfigureAwait(false);
            using (image)
            {
                BitmapSource frame = CaptureService.ToBitmapSource(image);
                frame.Freeze();
                return (frame, duration);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load a video thumbnail for {path}", ex);
            return (null, TimeSpan.Zero);
        }
    }

    /// <summary>Resolving an app icon walks the process list, so it happens off the UI thread
    /// alongside the thumbnails and lands as a fade-in once the tile is already up.</summary>
    private async Task LoadSourceAppIconsAsync(
        IEnumerable<HistoryItem> items,
        CancellationToken cancellationToken)
    {
        foreach (HistoryItem item in items.Where(item => item.SourceApp.Length > 0 && item.SourceAppIcon is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageSource? icon = await Task.Run(
                () => SourceAppIcons.For(item.SourceApp),
                cancellationToken).ConfigureAwait(false);
            if (icon is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() => item.SourceAppIcon = icon);
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

    private void UpdateEmptyState()
    {
        if (_closed) return;

        if (_items.Count > 0 && _loadError is null)
        {
            _emptyStateFade?.Dispose();
            _emptyStateFade = null;
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        HistoryEmptyState state = HistoryEmptyState.For(_filter, _settings.Current.HistoryEnabled, _loadError);
        EmptyStateGlyph.Text = state.Glyph;
        EmptyStateTitle.Text = state.Title;
        EmptyStateBody.Text = state.Body;
        NewCaptureButton.Visibility = state.ShowNewCapture ? Visibility.Visible : Visibility.Collapsed;
        RetryLoadButton.Visibility = state.ShowRetry ? Visibility.Visible : Visibility.Collapsed;

        bool wasVisible = EmptyState.Visibility == Visibility.Visible;
        EmptyState.Visibility = Visibility.Visible;
        if (wasVisible) return;

        // Arriving on an empty grid: fade the message in rather than dropping it in place the
        // instant the last card disappears.
        _emptyStateFade?.Dispose();
        EmptyState.Opacity = 0;
        _emptyStateFade = Motion.Run(
            Motion.SwapMs,
            Motion.EaseOutCubic,
            progress => EmptyState.Opacity = progress,
            () => _emptyStateFade = null);
    }

    private void OnNewCapture(object sender, RoutedEventArgs e) => NewCaptureRequested?.Invoke();

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
        SelectAllText.Text = EverythingSelected ? "Clear selection" : "Select all";
        SelectionSummaryText.Text = count == 0 ? string.Empty : $"{count:N0} selected    {HistoryItem.FormatFileSize(bytes)}";
        bool hasSelection = count > 0;
        CopyToButton.IsEnabled = hasSelection;
        ShareButton.IsEnabled = hasSelection;
        DeleteSelectedButton.IsEnabled = hasSelection;
        PinSelectedButton.IsEnabled = hasSelection && ItemsList.SelectedItems.Cast<HistoryItem>().Any(item => item.CanPin);
    }

    /// <summary>One button for the whole gesture. There used to be two next to each other —
    /// "All" and "Select all" — meaning the same thing; this one says which half it will do.</summary>
    private void OnToggleSelectAll(object sender, RoutedEventArgs e)
    {
        if (!_selectionMode) return;
        if (EverythingSelected)
            ItemsList.UnselectAll();
        else
            ItemsList.SelectAll();
        _selectionAnchor = _items.FirstOrDefault();
        UpdateSelectionState();
    }

    private bool EverythingSelected => _items.Count > 0 && ItemsList.SelectedItems.Count == _items.Count;

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
            _pendingSelectionClick = null;
            e.Handled = true;
            if (e.ClickCount > 1)
                return;

            // Grabbing a tile that is already part of the selection has to mean "drag all of
            // these", so its toggle waits for mouse-up — deciding at mouse-down would deselect
            // the tile in the first pixel of the drag. Shift- and Ctrl-clicks keep toggling
            // immediately: they are how the selection is built, never how it is dragged.
            if (Keyboard.Modifiers == ModifierKeys.None && ItemsList.SelectedItems.Contains(item))
            {
                _dragStart = e.GetPosition(null);
                _dragItem = item;
                _pendingSelectionClick = item;
                return;
            }

            ApplySelectionClick(item, Keyboard.Modifiers);
            return;
        }
        if (e.ClickCount == 2)
        {
            _dragItem = null;
            ActivateItem(item);
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

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        HistoryItem? pending = _pendingSelectionClick;
        _pendingSelectionClick = null;
        _dragItem = null;
        // The drag already consumed this gesture; toggling now would clear the selection the
        // user just dropped somewhere.
        if (pending is null || _dragging)
            return;

        ApplySelectionClick(pending, ModifierKeys.None);
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
        _pendingSelectionClick = null;
        // Windows draws only its generic file-drag cursor, so without this the user cannot see
        // which capture is in flight. One preview window per History, shown and hidden around
        // the drag, tracked from GiveFeedback exactly as the editor's drag-out pill does.
        DragPreview? preview = ShowDragPreview(item);
        var source = (UIElement)sender;
        void OnFeedback(object s, GiveFeedbackEventArgs args) => preview?.MoveToCursor();
        try
        {
            source.GiveFeedback += OnFeedback;
            var data = new DataObject(DataFormats.FileDrop, DragPaths(item.FilePath, LiveSelectionPaths()));
            DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            Log.Error($"Drag-out failed for {item.FilePath}", ex);
        }
        finally
        {
            source.GiveFeedback -= OnFeedback;
            preview?.Hide();
            _dragging = false;
            _dragItem = null;
        }
    }

    /// <summary>The selected paths that still exist on disk, or nothing at all outside
    /// selection mode.</summary>
    private IReadOnlyList<string> LiveSelectionPaths() =>
        _selectionMode
            ? SelectedItems().Select(entry => entry.FilePath).ToList()
            : Array.Empty<string>();

    /// <summary>Everything a drag is carrying. Grabbing a tile that is part of the current
    /// selection drags the whole selection; grabbing anything else drags only itself, so a
    /// selection made and then ignored cannot smuggle extra files into the drop.</summary>
    internal static string[] DragPaths(string grabbedPath, IReadOnlyList<string> selection)
    {
        foreach (string path in selection)
        {
            if (string.Equals(path, grabbedPath, StringComparison.OrdinalIgnoreCase))
                return selection.ToArray();
        }

        return new[] { grabbedPath };
    }

    private DragPreview? ShowDragPreview(HistoryItem item)
    {
        if (DragThumbnail(item.Thumbnail) is not BitmapSource thumbnail)
            return null;

        try
        {
            _dragPreview ??= new DragPreview();
            _dragPreview.Show(thumbnail, VisualTreeHelper.GetDpi(this).DpiScaleX);
            return _dragPreview;
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the history drag preview", ex);
            return null;
        }
    }

    /// <summary>The tile's own thumbnail, scaled down if it is a video frame (those decode at
    /// up to 640 px, which would follow the cursor as a full-size window).</summary>
    internal static BitmapSource? DragThumbnail(ImageSource? source)
    {
        if (source is not BitmapSource bitmap || bitmap.PixelWidth <= 0)
            return null;
        if (bitmap.PixelWidth <= DragPreviewMaxWidth)
            return bitmap;

        double scale = (double)DragPreviewMaxWidth / bitmap.PixelWidth;
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private const int DragPreviewMaxWidth = 200;

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { IsImage: true } item)
            await CopyImageAsync(item);
    }

    private async Task CopyImageAsync(HistoryItem item)
    {
        // Gone from disk (deleted under us, or a drive that went away): the watcher refresh
        // will drop the tile, so say nothing rather than blaming the clipboard.
        if (!File.Exists(item.FilePath))
            return;
        try
        {
            var bmp = await Task.Run(() => HistoryImageLoader.LoadDetachedBitmap(item.FilePath));
            await CaptureService.CopyToClipboardAsync(bmp, takeOwnership: true);
            // A History tile has nothing that changes when a copy lands, so both outcomes
            // used to look identical: the row sat there either way and only the log knew.
            App.Toast("Copied", "The capture is on the clipboard.", severity: ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to copy history item {item.FilePath}", ex);
            App.Toast("Copy failed", FriendlyError.For(ex), severity: ToastSeverity.Error);
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

    /// <summary>
    /// Rotate, Rename, Print and Open-with all shipped on the quick-actions card and were
    /// unreachable in History at any depth — so the only way to rotate a capture you already
    /// took was to restore it to the card first.
    /// </summary>
    private async void OnRotate(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { IsImage: true } item) return;
        if (QuickActionsMenu.TransformFor((sender as FrameworkElement)?.Tag as string ?? string.Empty)
            is not SD.RotateFlipType transform)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using SD.Bitmap image = HistoryImageLoader.LoadDetachedBitmap(item.FilePath);
                image.RotateFlip(transform);
                // Write to temp and swap: a half-written history file is worse than a failed
                // rotate, and staging inside the folder would flash up as a card of its own.
                string staging = Path.Combine(
                    Path.GetTempPath(),
                    $"winshot-rotate-{Guid.NewGuid():N}{Path.GetExtension(item.FilePath)}");
                ImageSaver.Save(image, staging);
                File.Move(staging, item.FilePath, overwrite: true);
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to rotate history item {item.FilePath}", ex);
            ThemedMessageDialog.Show(this, "Rotate failed", FriendlyError.For(ex));
            return;
        }

        // The folder watcher picks the rewrite up and re-decodes the thumbnail.
        await ReloadAsync();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;

        string suggestion = Path.GetFileNameWithoutExtension(item.FileName);
        while (true)
        {
            string? answer = ThemedMessageDialog.Prompt(
                this,
                "Rename capture",
                "WinShot keeps this capture's type tag and file extension.",
                suggestion,
                "Rename");
            if (answer is null) return;

            HistoryRename.Result result = HistoryRename.Resolve(item.FilePath, answer);
            if (result.Error is not null)
            {
                suggestion = answer;
                ThemedMessageDialog.Show(this, "Rename failed", result.Error);
                continue;
            }

            if (string.Equals(result.Path, item.FilePath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                File.Move(item.FilePath, result.Path!);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to rename history item {item.FilePath}", ex);
                ThemedMessageDialog.Show(this, "Rename failed", FriendlyError.For(ex));
            }

            _ = ReloadAsync();
            return;
        }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { IsImage: true } item || !File.Exists(item.FilePath)) return;
        try
        {
            using SD.Bitmap image = await Task.Run(() => HistoryImageLoader.LoadDetachedBitmap(item.FilePath));
            QuickActionsMenu.PrintImage(new WindowHandle(new WindowInteropHelper(this).Handle), image);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to print history item {item.FilePath}", ex);
            ThemedMessageDialog.Show(this, "Print failed", FriendlyError.For(ex));
        }
    }

    private void OnOpenWith(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item || !File.Exists(item.FilePath)) return;
        try
        {
            QuickActionsMenu.ShowOpenWith(item.FilePath);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to show Open with for {item.FilePath}", ex);
            ThemedMessageDialog.Show(this, "Open with failed", FriendlyError.For(ex));
        }
    }

    /// <summary>The WinForms print dialog wants an IWin32Window; History is WPF.</summary>
    private sealed class WindowHandle : WF.IWin32Window
    {
        public WindowHandle(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
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

    /// <summary>
    /// Enter and double-click: a still image goes back to the Quick Access card rather than
    /// out to the Windows Photos app. History stays open and the card restacks, so activating
    /// several in a row builds the stack — the multi-restore gesture. Video and GIF have no
    /// card, so they still hand off to the shell; "Open" in the context menu always does.
    /// </summary>
    private void ActivateItem(HistoryItem item)
    {
        if (item.IsImage)
            RestoreRequested?.Invoke(item.FilePath);
        else
            OpenItem(item);
    }

    private static void OpenItem(HistoryItem item)
    {
        if (!File.Exists(item.FilePath))
            return;
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
    private ImageSource? _sourceAppIcon;
    private TimeSpan _duration;

    public HistoryItem(string filePath)
    {
        FilePath = filePath;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ImageExtensions.Contains(ext);
        IsVideo = VideoExtensions.Contains(ext);
        IsGif = ext == ".gif";
        // "20260904-141233-091-scroll@chrome.png": the source-app tag is last, so strip it
        // before asking about the classification tag in front of it. Files written before
        // the tag existed simply have nothing after the last '-'.
        string stem = Path.GetFileNameWithoutExtension(filePath);
        int tag = stem.LastIndexOf('@');
        SourceApp = tag > 0 && tag < stem.Length - 1 ? stem[(tag + 1)..] : string.Empty;
        if (tag > 0) stem = stem[..tag];
        IsScrollingCapture = IsImage && stem.EndsWith("-scroll", StringComparison.OrdinalIgnoreCase);
        ExtensionLabel = ext.TrimStart('.').ToUpperInvariant();
        FileName = Path.GetFileName(filePath);
        CapturedAt = ParseCapturedAt(filePath);
        FileSize = TryGetFileSize(filePath);
        Caption = BuildCaption(CapturedAt, FileSize);
        RelativeTime = BuildRelativeTime(CapturedAt);
        string sizeText = FormatFileSize(FileSize);
        MetadataText = sizeText.Length == 0 ? ExtensionLabel : $"{sizeText} · {ExtensionLabel}";
    }

    public string FilePath { get; }
    public bool IsImage { get; }
    public bool IsVideo { get; }
    public bool IsGif { get; }
    public bool IsScrollingCapture { get; }

    /// <summary>Process that owned the window this capture was taken over, or empty when the
    /// file carries no tag (everything written before the tag existed, and every recording).</summary>
    public string SourceApp { get; }
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

    /// <summary>Clip length for the media bar: "17s" under a minute, then "2:05", then
    /// "1:02:05". Empty for a length that could not be read, so the bar shows only the size.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return string.Empty;
        // Round up: a 0.4s clip is "1s", not "0s".
        int seconds = (int)Math.Ceiling(duration.TotalSeconds);
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}:{seconds % 60:00}";
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
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
    public Visibility ThumbnailVisibility => IsImage || IsGif || Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The "MP4" text card, shown only until the real first frame lands (or for good,
    /// if the file has no frame a decoder will give us).</summary>
    public Visibility MediaFallbackVisibility => IsVideo && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingThumbnailVisibility => (IsImage || IsGif) && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sweeping highlight over the placeholder. Off when the user has turned
    /// animation off system-wide, which leaves the plain placeholder rather than a still
    /// gradient sitting in the corner of every tile.</summary>
    public Visibility ShimmerVisibility =>
        MissingThumbnailVisibility == Visibility.Visible && Motion.AnimationsEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility ScrollingVisibility => IsScrollingCapture ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Bar across the bottom of a video tile: camcorder glyph, clip length, file size.</summary>
    public Visibility MediaBarVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public string MediaBarText
    {
        get
        {
            string length = FormatDuration(Duration);
            string size = FormatFileSize(FileSize);
            if (length.Length == 0) return size;
            return size.Length == 0 ? length : $"{length}  ·  {size}";
        }
    }
    public Visibility GifBadgeVisibility => IsGif ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceAppVisibility => SourceAppIcon is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Tile tooltip. Dragging a tile into another app has always worked and has never
    /// been advertised anywhere, so the tooltip that already showed the file name says it.</summary>
    public string DragHint => $"{FileName}\nDrag into another app to copy the file there.";

    public string AccessibleName =>
        $"{(IsScrollingCapture ? "Scrolling screenshot" : IsImage ? "Screenshot" : ExtensionLabel)}" +
        $"{(SourceApp.Length == 0 ? string.Empty : $" from {SourceApp}")} captured {Caption}";
    public string AccessibleHelpText =>
        CanPin
            ? "Press Enter to restore, Ctrl+C to copy, Space to preview, or Tab for Copy, Edit, Pin, and Delete actions."
            : "Press Enter to open, Space to preview, or Tab for Delete. Pin is available only for still images.";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingThumbnailVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShimmerVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaFallbackVisibility)));
        }
    }

    /// <summary>How long a recording runs, once it has been read off the file. Zero until then
    /// (and for good, for anything that is not a video).</summary>
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaBarText)));
        }
    }

    /// <summary>Icon of the app this capture was taken over. Resolved off the UI thread with
    /// the thumbnail, so it arrives (and fades in) after the tile is already on screen.</summary>
    public ImageSource? SourceAppIcon
    {
        get => _sourceAppIcon;
        set
        {
            _sourceAppIcon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceAppIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceAppVisibility)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
