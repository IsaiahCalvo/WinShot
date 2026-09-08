using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
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
    /// <summary>Files with a rotate in flight; see <see cref="OnRotate"/>.</summary>
    private readonly HashSet<string> _rotating = new(StringComparer.OrdinalIgnoreCase);
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
    private string? _emptyStateKey;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watcherDebounce;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;
    public event Action<string>? RestoreRequested;
    public event Action<IReadOnlyList<string>>? MultiPinRequested;

    /// <summary>Raised by the empty state's "New capture" button — the answer to a grid with
    /// nothing in it is usually to take one. The window comes along so the handler can take it
    /// off screen for the capture.</summary>
    public event Action<HistoryWindow>? NewCaptureRequested;

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
            // A filter or a delete leaves the grid sliding for 200 ms. Closing on top of that has
            // to stop the tween here — its handle lives on the panel, nowhere else, and Unloaded
            // is queued rather than raised, so it would arrive long after the clock was needed back.
            FindDescendant<ReflowWrapPanel>(ItemsList)?.CancelReflow();
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
        // Escape's fade-out is still running: the window is visible, but on its way to Close().
        // Without this the press activates a window that closes itself a moment later, and the
        // user has to press the hotkey a second time.
        if (instance._dismissing)
        {
            instance._windowFade?.Dispose();
            instance._windowFade = null;
            instance._dismissing = false;
            // Ramp back up from wherever the fade-out had got to. Assigning Opacity = 1 here made
            // a half-faded window jump to fully opaque in one frame before anything else ran.
            instance.FadeIn(instance.Opacity);
        }

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
    /// <param name="from">Opacity to ramp up from: 0 for a window arriving, and the live value
    /// when recovering one that was part-way through fading out, so it continues rather than
    /// snapping to full brightness first.</param>
    private void FadeIn(double from = 0d)
    {
        _windowFade?.Dispose();
        Opacity = from;
        bool finished = false;
        IDisposable handle = Motion.Run(
            Motion.EnterDurationMs,
            Motion.EaseOutCubic,
            progress => Opacity = from + ((1d - from) * progress),
            () =>
            {
                finished = true;
                _windowFade = null;
            });
        _windowFade = finished ? null : handle;
    }

    /// <summary>
    /// Escape: fade out from wherever the window currently is, then close. The exit is
    /// deliberately quicker than the entrance (SwapMs against EnterDurationMs) — leaving should
    /// not make the user wait — but it starts where the entrance had reached, so pressing Escape
    /// on top of the opening fade keeps dimming instead of flashing to opaque first.
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
        double from = Opacity;
        // With animation off system-wide Motion.Run finishes before it returns, so the handle must
        // not be stored onto a window Close() has already torn down — the same guard FadeIn uses.
        bool finished = false;
        IDisposable handle = Motion.Run(
            Motion.SwapMs,
            Motion.EaseInCubic,
            progress => Opacity = from * (1d - progress),
            () =>
            {
                finished = true;
                _windowFade = null;
                Close();
            });
        _windowFade = finished ? null : handle;
    }

    private static void CreateInstance(HistoryService history, SettingsService settings)
    {
        _instance = new HistoryWindow(history, settings);
        _instance.Closed += (_, _) => _instance = null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        }

        return null;
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
        // WPF reports F10 as Key.System because it is Windows' menu key; unwrap just that one so
        // Shift+F10 reaches the key map, without turning every Alt+<key> combo into a bare key.
        Key key = e.Key == Key.System && e.SystemKey == Key.F10 ? Key.F10 : e.Key;
        if (HandleKey(key, Keyboard.Modifiers))
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
            // Rotate, Rename, Print and Open-with live only on the tile's context menu, and
            // that menu hangs off the card INSIDE the item template. ContextMenuService walks
            // UP from the focused element, and what has focus is the ListBoxItem above the
            // card, so Windows' own menu gestures found nothing there. (Hoisting the menu onto
            // the container is not the fix: a Style setter shares one menu instance across
            // every tile.)
            case Key.Apps:
            case Key.F10 when modifiers == ModifierKeys.Shift:
                return OpenSelectedItemMenu();
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

    /// <summary>Opens the focused tile's context menu from the keyboard, anchored to the tile
    /// so it appears where a right-click would have put it.</summary>
    private bool OpenSelectedItemMenu()
    {
        if (SelectedItem() is not { } item) return false;
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            return false;
        if (MenuInside(container) is not { } menu) return false;

        // The ContextMenu lives in the item's DataTemplate, so these two writes stick to that
        // tile. ContextMenuService resets PlacementTarget on a later right-click but NOT
        // Placement, so without putting it back one Shift+F10 left that tile's menu opening
        // under the card for the rest of the session instead of at the cursor.
        System.Windows.Controls.Primitives.PlacementMode placement = menu.Placement;
        void Restore(object? s, RoutedEventArgs e)
        {
            menu.Closed -= Restore;
            menu.Placement = placement;
        }

        menu.Closed += Restore;
        menu.PlacementTarget = container;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    private static ContextMenu? MenuInside(DependencyObject root)
    {
        if (root is FrameworkElement element && element.ContextMenu is { } menu)
            return menu;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            if (MenuInside(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        }

        return null;
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
        // Tunneling so the chips/scroll viewer never swallow Space — but only while focus is in
        // the grid. Claiming it window-wide meant Space never reached a focused control, so
        // tabbing to the empty state's "New capture" or "Try again" and pressing it opened a
        // quick preview of nothing instead of activating the button.
        if (e.Key == Key.Space && (ItemsList.IsKeyboardFocusWithin || _previewOpen))
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

    /// <summary>
    /// Works out which <see cref="HistoryItem"/>s the next page is built from, given the files
    /// on disk and the items already loaded. A file that is still there keeps the SAME item, and
    /// so keeps its decoded thumbnail and its card: handing <c>ReplaceAll</c> brand-new instances
    /// made every visible tile a removal plus an insertion, so one capture landing while the
    /// window was open blanked the whole grid back to shimmer and faded all of it in again.
    ///
    /// ...but only while the bytes behind that path are still the ones the item was built from.
    /// A rotate rewrites the file under its own name, and reusing the item there kept the
    /// thumbnail decoded from the old pixels, so the rotate was invisible until the window was
    /// closed and reopened.
    ///
    /// Pure and static because it is expensive and must not run on the UI thread: it is one
    /// <see cref="FileInfo"/> stat plus four string formats per new file, and a second stat per
    /// kept one. Inside the dispatcher that is ~400 ms of frozen window for a 1,000-file library
    /// and ~5 s at 5,000, on the very refresh path the incremental-reload work exists to smooth.
    /// </summary>
    internal static (List<HistoryItem> Items, HashSet<HistoryItem> Live) DiffItems(
        IReadOnlyList<string> files,
        IReadOnlyList<HistoryItem> previousItems)
    {
        var kept = new Dictionary<string, HistoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (HistoryItem previous in previousItems)
            kept[previous.FilePath] = previous;

        var newItems = new List<HistoryItem>(files.Count);
        var live = new HashSet<HistoryItem>();
        foreach (string file in files)
        {
            HistoryItem item = kept.TryGetValue(file, out HistoryItem? existing) && Reusable(existing)
                ? existing
                : new HistoryItem(file);
            newItems.Add(item);
            live.Add(item);
        }

        return (newItems, live);

        // An item is worth keeping while the bytes it was built from are still on disk, or while
        // it has not read them yet. "Has not read them yet" is Thumbnail == null AND neither
        // decode pass has been tried: once a pass has run and come back empty the item carries a
        // permanent "can't read this picture", and reusing it made that permanent for the FILE
        // too. A capture copied into the folder is stat'd and decoded mid-write, fails, and then
        // the completion event's reload reused the failed item forever — the tile stayed on the
        // warning glyph over a perfectly readable file, through the watcher, Refresh and Try
        // again alike, until History was closed and reopened.
        static bool Reusable(HistoryItem existing) =>
            (existing.Thumbnail is null && !existing.ThumbnailTried && !existing.VideoFrameTried)
            || existing.MatchesFileOnDisk();
    }

    private async Task ReloadAsync()
    {
        // Rename and Rotate are async void and reload after their awaits, so a reload can start
        // after the window has gone. Without this it refills the paging state, regenerates
        // containers for a closed window and decodes a page of thumbnails nothing will show.
        if (_closed) return;

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var files = await Task.Run(_history.GetItems).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;

            cts.Token.ThrowIfCancellationRequested();
            // Snapshot on the UI thread — two list copies, no file I/O — then leave it. The
            // diff itself stats every file and is the one part of a reload that is genuinely
            // expensive; see DiffItems.
            (string? selectedPath, List<HistoryItem> previousItems) = await Dispatcher.InvokeAsync(
                () => (SelectedItem()?.FilePath, _paging.AllItems.ToList()));
            cts.Token.ThrowIfCancellationRequested();

            (List<HistoryItem> newItems, HashSet<HistoryItem> live) =
                await Task.Run(() => DiffItems(files, previousItems), cts.Token).ConfigureAwait(false);

            IReadOnlyList<HistoryItem> visibleItems = Array.Empty<HistoryItem>();
            await Dispatcher.InvokeAsync(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                if (_closed) return;
                _loadError = null;

                HistoryItem? restored = _paging.ReplaceAll(
                    newItems,
                    MatchesFilter,
                    selectedPath,
                    cts.Token,
                    CurrentComparer());
                // Only the items that really went away release their bitmap — which now includes
                // the ones replaced because their file changed underneath them.
                foreach (HistoryItem previous in previousItems)
                {
                    if (!live.Contains(previous))
                        previous.Thumbnail = null;
                }

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
                if (_closed) return;
                // A refresh that fails over a page that is already up must not raise the panel:
                // it is centred over the still-visible cards and takes the clicks aimed at them,
                // so the user reads "can't read your folder" across a grid full of captures.
                // Keep the last good page and say it in a toast; the panel is for a grid that
                // really is empty.
                if (_items.Count > 0)
                {
                    App.Toast(
                        "Can't read your history folder",
                        FriendlyError.For(ex),
                        severity: ToastSeverity.Error);
                    return;
                }

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

    /// <summary>The tile's thumbnail frame, in DIPs (HistoryWindow.xaml's card width).</summary>
    private const double TileThumbnailWidth = 260;

    /// <summary>
    /// How wide a tile thumbnail decodes, in real pixels. The frame is 260 DIP, so a fixed 180 px
    /// decode was a 1.44× upscale at 100 %, 2.17× at 150 % and 2.89× at 200 % — a soft grid on
    /// exactly the frame the shimmer and the fade-up put the user's eye on. The drag ghost already
    /// sizes its decode this way; this is the call site that was left on the default.
    ///
    /// Capped at 2× because a page is 200 tiles and every one of them is a live bitmap: 260 DIP
    /// at 200 % is already ~136 MB of thumbnails, and a 300 % monitor would take it past 300.
    /// Beyond that cap the grid is very slightly soft, which is the cheaper of the two costs.
    /// </summary>
    internal static int TileThumbnailPixelWidth(double dpiScale) =>
        (int)Math.Round(TileThumbnailWidth * Math.Clamp(dpiScale, 1d, 2d));

    private static BitmapImage? TryLoadThumbnail(string path, int decodePixelWidth = 180)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = decodePixelWidth;
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
        // ThumbnailTried, not just "no thumbnail yet": a file no codec will open (a truncated PNG,
        // a .webp on a machine without the WebP codec) would otherwise sit in this set forever —
        // shimmering "loading" for the life of the window, and re-decoding on every reload, filter
        // change and delete. The video pass has had the same flag since it hit the same wall.
        var pending = items
            .Where(item => (item.IsImage || item.IsGif) && item.Thumbnail is null && !item.ThumbnailTried)
            .ToList();
        if (pending.Count > 0)
        {
            int decodeWidth = await Dispatcher.InvokeAsync(
                () => TileThumbnailPixelWidth(VisualTreeHelper.GetDpi(this).DpiScaleX));

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
                        () => TryLoadThumbnail(item.FilePath, decodeWidth),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Set first: the flag is what collapses the shimmer and raises the
                        // "can't read this" mark when the decode came back with nothing.
                        item.ThumbnailTried = true;
                        item.Thumbnail = thumbnail;
                    });
                }
                finally
                {
                    decoding.Release();
                }
            })).ConfigureAwait(false);
        }

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
        foreach (HistoryItem item in items.Where(
                     item => item.IsVideo && item.Thumbnail is null && !item.VideoFrameTried))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(item.FilePath)) continue;

            (BitmapSource? frame, TimeSpan duration) = await LoadVideoFrameAsync(item.FilePath).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(() =>
            {
                item.VideoFrameTried = true;
                item.Duration = duration;
                // VideoThumbnail hands back a grey play-card for a clip it could not open, so in
                // practice this always assigns; the null branch is the belt for a decoder that
                // hands back nothing at all, and leaves the "MP4" card up rather than a blank tile.
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
        // SourceAppIcons deliberately does not cache a miss — an app that starts later should get
        // its icon — but a miss costs a full process enumeration, and this pass runs on every
        // reload, filter click, delete and Load more. Twenty tiles tagged with an app that is no
        // longer running meant twenty sweeps for the same answer; one per distinct name is enough
        // for the pass, and the next pass asks again.
        var missed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HistoryItem item in items.Where(item => item.SourceApp.Length > 0 && item.SourceAppIcon is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (missed.Contains(item.SourceApp)) continue;
            ImageSource? icon = await Task.Run(
                () => SourceAppIcons.For(item.SourceApp),
                cancellationToken).ConfigureAwait(false);
            if (icon is null)
            {
                missed.Add(item.SourceApp);
                continue;
            }
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

    /// <summary>
    /// Owner for every dialog this window raises. Rotate, Print, Delete and Delete-selected all
    /// await before they can fail, and Escape closes History on a fade — so the failure dialog can
    /// arrive after the owner is gone, and WPF throws "Cannot set Owner property to a Window that
    /// has been closed" for that. Three of those handlers are <c>async void</c>, so the throw would
    /// leave through the dispatcher rather than a catch. An ownerless dialog still shows.
    /// </summary>
    private Window? DialogOwner => _closed ? null : this;

    private void UpdateEmptyState()
    {
        if (_closed) return;

        if (_items.Count > 0 && _loadError is null)
        {
            if (EmptyState.Visibility != Visibility.Visible) return;

            // Leaving is the entrance played backwards. A bare Collapsed made the message vanish
            // under the first card of the page that just arrived.
            _emptyStateFade?.Dispose();
            _emptyStateKey = null;
            // The panel keeps its slot for the whole fade, in the middle of the grid, so its
            // buttons would otherwise eat a click aimed at a card that has just arrived under it.
            EmptyState.IsHitTestVisible = false;
            double leaving = EmptyState.Opacity;
            bool hidden = false;
            IDisposable exit = Motion.Run(
                Motion.SwapMs,
                Motion.EaseInCubic,
                progress => EmptyState.Opacity = leaving * (1d - progress),
                () =>
                {
                    hidden = true;
                    _emptyStateFade = null;
                    EmptyState.Visibility = Visibility.Collapsed;
                });
            _emptyStateFade = hidden ? null : exit;
            return;
        }

        HistoryEmptyState state = HistoryEmptyState.For(_filter, _settings.Current.HistoryEnabled, _loadError);
        EmptyStateGlyph.Text = state.Glyph;
        EmptyStateTitle.Text = state.Title;
        EmptyStateBody.Text = state.Body;
        NewCaptureButton.Visibility = state.ShowNewCapture ? Visibility.Visible : Visibility.Collapsed;
        RetryLoadButton.Visibility = state.ShowRetry ? Visibility.Visible : Visibility.Collapsed;
        OpenSettingsButton.Visibility = state.ShowSettings ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateActions.Visibility = state.ShowNewCapture || state.ShowRetry || state.ShowSettings
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Moving between two empty filters (Videos → GIFs) swaps three lines of copy in place, and
        // a failed "Try again" rewrites them to the same words. Fading again is what says the app
        // answered at all; a hard cut in place reads as nothing having happened.
        string key = state.Title + "\n" + state.Body;
        bool visible = EmptyState.Visibility == Visibility.Visible;
        bool sameReason = visible && _emptyStateKey == key;
        _emptyStateKey = key;
        EmptyState.Visibility = Visibility.Visible;
        EmptyState.IsHitTestVisible = true;
        if (sameReason) return;

        // Arriving on an empty grid: fade the message in rather than dropping it in place the
        // instant the last card disappears. A panel already up dips rather than starting from
        // black, so a swapped reason reads as the text changing, not the window blinking.
        _emptyStateFade?.Dispose();
        double from = visible ? 0.35d : 0d;
        EmptyState.Opacity = from;
        bool shown = false;
        IDisposable entrance = Motion.Run(
            Motion.SwapMs,
            Motion.EaseOutCubic,
            progress => EmptyState.Opacity = from + ((1d - from) * progress),
            () =>
            {
                shown = true;
                _emptyStateFade = null;
            });
        _emptyStateFade = shown ? null : entrance;
    }

    private void OnNewCapture(object sender, RoutedEventArgs e) => NewCaptureRequested?.Invoke(this);

    /// <summary>The button on the "History is turned off" panel: the Keep history slider lives on
    /// the Advanced tab, and telling the user to go find it is not an answer History can offer
    /// when it can just open the window.</summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        SettingsUi.SettingsWindow.Show(_settings).SelectAdvancedTab();

    /// <summary>
    /// Steps out of the frame for a capture started from the empty state. The region selector
    /// freezes the screen as it is, and History is a 1200x760 window in the middle of it, so
    /// leaving it up means the user drags a region over History's own empty state.
    /// </summary>
    public void HideForCapture()
    {
        _windowFade?.Dispose();
        _windowFade = null;
        // The empty panel owns the button that started this capture, so its entrance fade is
        // exactly what is running when it is pressed — and a tween left ticking against a hidden
        // window holds the 1 ms timer period for nothing.
        _emptyStateFade?.Dispose();
        _emptyStateFade = null;
        EmptyState.Opacity = 1;
        Opacity = 1;
        Hide();
    }

    /// <summary>The other half of <see cref="HideForCapture"/>: back on the same fade the window
    /// opens with, so the return is not a snap either.</summary>
    public void ShowAfterCapture()
    {
        if (_closed) return;
        Show();
        FadeIn();
        Activate();
        // Hiding stopped the folder watcher, and Show() builds a fresh one that by definition
        // knows nothing about files written while it was gone — which is precisely the interval
        // the capture this button asked for lands in. Without this the grid comes back reading
        // "No captures yet" behind a Quick Access card holding the shot. Same background refresh
        // the static Show path runs when it reveals a hidden window; the diffed reload keeps the
        // cards that are already up.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => _ = RefreshOnShowAsync()));
    }

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
        // An explicit AutomationProperties.Name overrides the content, so a fixed one made the
        // toggle invisible to a screen reader — the same string in both halves of the one button
        // this track merged the two into. SortDirectionButton keeps its name in step the same way.
        AutomationProperties.SetName(AllSelectionButton, SelectAllText.Text);
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
            ThemedMessageDialog.Show(DialogOwner, "Copy failed", "WinShot could not copy the selected files to the clipboard.");
            return;
        }

        // Same confirmation the single-item copy raises, worded for files.
        App.Toast(
            "Copied",
            selected.Count == 1 ? "1 file is on the clipboard." : $"{selected.Count} files are on the clipboard.",
            severity: ToastSeverity.Success);
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
                        ThemedMessageDialog.Show(DialogOwner, "Blip can't receive shares",
                            "Blip is running but is no longer accepting handoffs. Restart Blip and try again.");
                        break;
                    case BlipShareResult.Failed:
                        ThemedMessageDialog.Show(DialogOwner, "Share failed", "WinShot could not hand these files to Blip.");
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
            Dispatcher.BeginInvoke(new Action(() => ThemedMessageDialog.Show(DialogOwner, "Share failed", "WinShot could not prepare these files for sharing.")));
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
            ThemedMessageDialog.Show(DialogOwner, "Delete incomplete", $"WinShot could not delete {failed.Count:N0} selected item{(failed.Count == 1 ? string.Empty : "s")}.");
    }

    // ---- Tile actions ----

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        // "Try again" on a folder that is still unreachable rewrites the panel with the same
        // words. Forget the reason so the re-fade runs and the button is visibly not dead.
        _emptyStateKey = null;
        await ReloadAsync();
    }

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
        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The tile's own thumbnail first: it decodes at TileThumbnailPixelWidth (260 x the scale),
        // which is already larger than the ghost's cap at every scale WinShot runs at, so it is
        // both the right size and free. Decoding from disk here instead cost ~110 ms on the UI
        // thread for a tall scrolling capture — landing exactly where the user has already
        // committed to the gesture — and is now only the fallback for a tile that has none yet.
        BitmapSource? thumbnail =
            DragPreview.Fit(item.Thumbnail, dpiScale)
            ?? DragPreview.Fit(TryLoadThumbnail(item.FilePath, DragPreview.MaxEdgePixels(dpiScale)), dpiScale);
        if (thumbnail is null)
            return null;

        try
        {
            _dragPreview ??= new DragPreview();
            _dragPreview.Show(thumbnail, dpiScale);
            return _dragPreview;
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the history drag preview", ex);
            return null;
        }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { IsImage: true } item)
            await CopyImageAsync(item);
    }

    private async Task CopyImageAsync(HistoryItem item)
    {
        // Gone from disk (deleted under us, or a drive that went away). The watcher refresh drops
        // the tile eventually, but it is debounced and only runs while the window is up, so the
        // keypress would otherwise land on a tile that is still there and answer with nothing.
        if (!MissingFileToast(item, "Copy failed")) return;
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

        // A rotate is several hundred milliseconds of load, rotate, save and swap on a large PNG,
        // with nothing on the tile saying so — which is the shape that invites a second click.
        // Two runs on the same file each stage their own copy and each move it over the
        // destination, so the pair lands as 180 degrees or as a "Rotate failed" from one load
        // reading the file mid-swap. Keyed on the path, not the item, because a reload landing in
        // the middle replaces the instance.
        if (!_rotating.Add(item.FilePath)) return;

        try
        {
            // Write to temp and swap: a half-written history file is worse than a failed
            // rotate, and staging inside the folder would flash up as a card of its own.
            string staging = Path.Combine(
                Path.GetTempPath(),
                $"winshot-rotate-{Guid.NewGuid():N}{Path.GetExtension(item.FilePath)}");
            try
            {
                await Task.Run(() =>
                {
                    using (SD.Bitmap image = HistoryImageLoader.LoadDetachedBitmap(item.FilePath))
                    {
                        image.RotateFlip(transform);
                        ImageSaver.Save(image, staging);
                    }

                    MoveOverBusyFile(staging, item.FilePath);
                });
            }
            finally
            {
                // The swap either consumed the staging file or threw with it still on disk; a
                // failed rotate must not leave a copy of the capture in %TEMP% for good.
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch (Exception ex) { Log.Error($"Failed to clean up rotate staging file {staging}", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to rotate history item {item.FilePath}", ex);
            ThemedMessageDialog.Show(DialogOwner, "Rotate failed", FriendlyError.For(ex));
            return;
        }
        finally
        {
            _rotating.Remove(item.FilePath);
        }

        // The reload notices the file changed underneath the item and re-decodes it; the toast
        // is what says so before the new pixels land, and covers a rotate that changes so little
        // of the tile at 180 px that the user cannot tell it ran.
        App.Toast("Rotated", $"Saved to {item.FileName}.", severity: ToastSeverity.Success);
        await ReloadAsync();
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="staging"/>, waiting out a
    /// reader that has the destination open. History decodes up to
    /// <see cref="ThumbnailDecodeParallelism"/> thumbnails at once and a decode in flight holds
    /// its file, so a rotate fired right after a reload used to fail outright with "access is
    /// denied" — the card's rotate has <c>WaitForRenderIdleAsync</c> for the same reason, and the
    /// grid had no equivalent. A decode is milliseconds; anything still holding the file after
    /// the last try is a real conflict and the exception is the user's answer.
    /// </summary>
    private static void MoveOverBusyFile(string staging, string destination, bool overwrite = true)
    {
        const int attempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staging, destination, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(40);
            }
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;

        // The tags WinShot appended ("@chrome", "-scroll") are not the user's to retype: offering
        // the whole stem meant pressing Enter on the default either errored on '@' or stacked a
        // second "-scroll" on the name.
        string suggestion = HistoryRename.SuggestedName(item.FilePath);
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
                ThemedMessageDialog.Show(DialogOwner, "Rename failed", result.Error);
                continue;
            }

            if (string.Equals(result.Path, item.FilePath, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                // Same wait as Rotate, and off the UI thread for the same reason: a thumbnail
                // decode in flight holds the source file, so a rename fired right after a reload
                // used to answer "access is denied" — and waiting it out on the dispatcher froze
                // the window for the whole retry budget with the dialog already dismissed.
                await Task.Run(() => MoveOverBusyFile(item.FilePath, result.Path!, overwrite: false));
            }
            catch (Exception ex)
            {
                // A locked file or a name Windows refuses for a reason the resolver cannot see:
                // keep what the user typed and let them adjust it, the way a validation error does.
                Log.Error($"Failed to rename history item {item.FilePath}", ex);
                ThemedMessageDialog.Show(DialogOwner, "Rename failed", FriendlyError.For(ex));
                suggestion = answer;
                continue;
            }

            // Nothing on a tile shows the file name, so without this the rename is
            // indistinguishable from having cancelled the dialog.
            App.Toast(
                "Renamed",
                $"Saved as {Path.GetFileName(result.Path!)}.",
                severity: ToastSeverity.Success);
            _ = ReloadAsync();
            return;
        }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { IsImage: true } item) return;
        if (!MissingFileToast(item, "Print failed")) return;
        try
        {
            using SD.Bitmap image = await Task.Run(() => HistoryImageLoader.LoadDetachedBitmap(item.FilePath));
            QuickActionsMenu.PrintImage(new WindowHandle(new WindowInteropHelper(this).Handle), image);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to print history item {item.FilePath}", ex);
            ThemedMessageDialog.Show(DialogOwner, "Print failed", FriendlyError.For(ex));
        }
    }

    /// <summary>
    /// Answers the "the file went away under us" case the way every other action on this window
    /// does — with a toast naming the reason. Returns false when the capture is gone, so callers
    /// read as <c>if (!MissingFileToast(...)) return;</c>. Print and Open with… used to return in
    /// silence here, which left clicking again as the user's only available response.
    /// </summary>
    private static bool MissingFileToast(HistoryItem item, string title)
    {
        if (File.Exists(item.FilePath)) return true;
        App.Toast(title, "That capture is no longer in your history folder.", severity: ToastSeverity.Error);
        return false;
    }

    private void OnOpenWith(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        if (!MissingFileToast(item, "Open with failed")) return;
        try
        {
            QuickActionsMenu.ShowOpenWith(item.FilePath);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to show Open with for {item.FilePath}", ex);
            ThemedMessageDialog.Show(DialogOwner, "Open with failed", FriendlyError.For(ex));
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
            App.Toast("Couldn't open Explorer", FriendlyError.For(ex), severity: ToastSeverity.Error);
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
                ThemedMessageDialog.Show(DialogOwner, "Delete failed",
                    $"WinShot could not delete this history item.\n\n{detail}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete history item {item.FilePath}", ex);
            ThemedMessageDialog.Show(DialogOwner, "Delete failed", FriendlyError.For(ex));
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
        // Enter on a recording with no file association produced nothing at all — no window, no
        // message — while every other action on this window says both outcomes.
        if (!MissingFileToast(item, "Couldn't open the capture")) return;

        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open {item.FilePath}", ex);
            App.Toast("Couldn't open the capture", FriendlyError.For(ex), severity: ToastSeverity.Error);
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
    private bool _thumbnailTried;
    private ImageSource? _sourceAppIcon;
    private TimeSpan _duration;
    private readonly DateTime _writtenUtc;

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
        (FileSize, _writtenUtc) = TryReadStamp(filePath);
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

    private static (long Size, DateTime WrittenUtc) TryReadStamp(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch { return (0, DateTime.MinValue); }
    }

    /// <summary>
    /// True while the file on disk is still the one this item was built from. Rotate, and the
    /// quick-actions card's rotate/flip/resize, rewrite a history file in place under its own
    /// name — a refresh that matched on path alone kept the item, and with it the thumbnail
    /// decoded from the old pixels, so the grid was byte-for-byte identical after the edit.
    /// </summary>
    internal bool MatchesFileOnDisk()
    {
        (long size, DateTime writtenUtc) = TryReadStamp(FilePath);
        return size == FileSize && writtenUtc == _writtenUtc;
    }

    /// <summary>Set once a video's first frame has been attempted, successfully or not. Without
    /// it an MP4 no decoder will open sits in the pending set forever and is retried on every
    /// reload, filter change and delete — ahead of the app-icon pass, which waits behind it.</summary>
    internal bool VideoFrameTried { get; set; }

    /// <summary>The still-image twin of <see cref="VideoFrameTried"/>: set once the decode has
    /// been attempted, successfully or not. It keeps a file no codec will open out of the pending
    /// set, and it is what turns the placeholder from a shimmer that promises a picture into a
    /// static mark that says one is not coming.</summary>
    internal bool ThumbnailTried
    {
        get => _thumbnailTried;
        set
        {
            if (_thumbnailTried == value) return;
            _thumbnailTried = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShimmerVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadableVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderGlyph)));
        }
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
    public Visibility PinVisibility => CanPin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThumbnailVisibility => IsImage || IsGif || Thumbnail is not null ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>The "MP4" text card, shown only until the real first frame lands (or for good,
    /// if the file has no frame a decoder will give us).</summary>
    public Visibility MediaFallbackVisibility => IsVideo && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingThumbnailVisibility => (IsImage || IsGif) && Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sweeping highlight over the placeholder. Off when the user has turned
    /// animation off system-wide, which leaves the plain placeholder rather than a still
    /// gradient sitting in the corner of every tile — and off once the decode has been tried
    /// and failed, because a shimmer is a promise that a picture is coming.</summary>
    public Visibility ShimmerVisibility =>
        MissingThumbnailVisibility == Visibility.Visible && !ThumbnailTried && Motion.AnimationsEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>The decode finished and came back with nothing: the placeholder settles into a
    /// static "can't read this" mark instead of sweeping forever over a file that will never
    /// load. Same shape as the "MP4" card a video with no readable frame keeps.</summary>
    public Visibility UnreadableVisibility =>
        MissingThumbnailVisibility == Visibility.Visible && ThumbnailTried
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Segoe MDL2 picture while the tile is still loading, warning once it has given
    /// up — the same two glyphs the empty state uses for the same two meanings.</summary>
    // Segoe MDL2: picture while it loads, warning once it has given up. Spelled as escapes
    // to stay greppable next to HistoryEmptyState, which picks the same two glyphs for the
    // same two meanings; the literal private-use characters read as boxes in a diff.
    public const string LoadingGlyph = "\uE91B";
    public const string UnreadableGlyph = "\uE7BA";

    public string PlaceholderGlyph => ThumbnailTried ? UnreadableGlyph : LoadingGlyph;
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
    /// <summary>The badge sits bottom-right, which is where the video bar runs. Nothing stamps an
    /// app tag on a recording today, but a hand-placed one would drop the icon on top of the clip
    /// length, so the bar wins.</summary>
    public Visibility SourceAppVisibility =>
        SourceAppIcon is null || MediaBarVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>Tile tooltip. Dragging a tile into another app has always worked and has never
    /// been advertised anywhere, so the tooltip that already showed the file name says it.</summary>
    public string DragHint => $"{FileName}\nDrag into another app to copy the file there.";

    public string AccessibleName =>
        $"{(IsScrollingCapture ? "Scrolling screenshot" : IsImage ? "Screenshot" : ExtensionLabel)}" +
        $"{(SourceApp.Length == 0 ? string.Empty : $" from {SourceApp}")} captured {Caption}";
    public string AccessibleHelpText =>
        CanPin
            // The overlay row a Tab lands in opens with Restore, so leaving it out of the
            // announcement put the first button somewhere the user was not told about.
            ? "Press Enter to restore, Ctrl+C to copy, Space to preview, or Tab for Restore, Copy, Edit, Pin, and Delete actions."
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadableVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderGlyph)));
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
