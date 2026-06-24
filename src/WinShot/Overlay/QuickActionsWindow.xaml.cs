using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Overlay;

/// <summary>
/// CleanShot-style floating thumbnail shown after every capture. Stacks
/// bottom-right of the primary work area. Owns its bitmap and disposes it on
/// close — consumers that outlive the overlay must take CloneImage().
/// Press-and-drag on the thumbnail starts a FileDrop drag-out into other apps;
/// dragging the panel chrome still moves the window. Once focused (click it),
/// C/S/E/P/O trigger the corresponding action and Esc closes.
/// </summary>
public partial class QuickActionsWindow : Window
{
    private static readonly List<QuickActionsWindow> OpenWindows = new();

    /// <summary>History paths of recently closed overlays, most recent on top.</summary>
    private static readonly Stack<string> RecentlyClosed = new();

    private readonly SD.Bitmap _image;
    private readonly SettingsService _settings;
    private readonly Task? _releaseAfterTask;
    private readonly bool _requestMemoryCleanupOnClose;
    private readonly bool _loadThumbnail;
    private Task? _thumbnailTask;
    private string? _tempDragPath;
    private string? _historyPath;
    private Task<string>? _dragFileTask;
    private Task? _copyTask;
    private bool _dragArmed;
    private bool _closed;
    private bool _thumbnailStarted;
    private Point _dragStart;

    public event Action<QuickActionsWindow>? EditRequested;
    public event Action<QuickActionsWindow>? PinRequested;
    public event Action<QuickActionsWindow>? OcrRequested;
    public event Action<QuickActionsWindow>? BackgroundRequested;

    public QuickActionsWindow(
        SD.Bitmap image,
        SettingsService settings,
        string? historyPath = null,
        Task<string?>? historyPathTask = null)
        : this(image, settings, historyPath, historyPathTask, loadThumbnail: true)
    {
    }

    private QuickActionsWindow(
        SD.Bitmap image,
        SettingsService settings,
        string? historyPath,
        Task<string?>? historyPathTask,
        bool loadThumbnail,
        Task? releaseAfterTask = null,
        bool requestMemoryCleanupOnClose = true)
    {
        ThemeResources.EnsureLoaded();
        InitializeComponent();
        _image = image;
        _settings = settings;
        _releaseAfterTask = releaseAfterTask;
        _requestMemoryCleanupOnClose = requestMemoryCleanupOnClose;
        _loadThumbnail = loadThumbnail;
        _historyPath = historyPath;
        SetThumbnailPlaceholder(image);

        OpenWindows.Add(this);
        Loaded += (_, _) => PositionBottomRight();
        ContentRendered += (_, _) => StartThumbnailLoad();
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            _closed = true;
            Thumb.Source = null;
            if (_historyPath is not null)
                PushRecentlyClosed(_historyPath);
            DisposeImageWhenUnused();
        };

        if (historyPathTask is not null)
            _ = WatchHistoryPathAsync(historyPathTask);

        int seconds = settings.Current.OverlayAutoCloseSeconds;
        if (seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    private void StartThumbnailLoad()
    {
        if (!_loadThumbnail || _thumbnailStarted || _closed)
            return;

        _thumbnailStarted = true;
        _thumbnailTask = LoadThumbnailAsync(_image);
    }

    public static void Prewarm(SettingsService settings)
    {
        var bitmap = new SD.Bitmap(1, 1);
        var window = new QuickActionsWindow(
            bitmap,
            settings,
            historyPath: null,
            historyPathTask: null,
            loadThumbnail: false,
            requestMemoryCleanupOnClose: false);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (window.IsVisible)
                window.Close();
        };
        window.ContentRendered += (_, _) =>
        {
            fallback.Stop();
            window.Close();
        };
        window.Show();
        fallback.Start();
    }

    public static QuickActionsWindow CreateWithDeferredImageRelease(
        SD.Bitmap image,
        SettingsService settings,
        Task<string?> historyPathTask,
        Task releaseAfterTask)
        => new(
            image,
            settings,
            historyPath: null,
            historyPathTask,
            loadThumbnail: true,
            releaseAfterTask);

    public SD.Bitmap CloneImage() => CaptureService.CloneBitmap(_image);

    private void DisposeImageWhenUnused()
    {
        Task? pending = PendingImageUseTask();
        if (pending is null || pending.IsCompleted)
        {
            _image.Dispose();
            if (_requestMemoryCleanupOnClose)
                MemoryCleanup.Request();
            return;
        }

        _ = DisposeImageAfterAsync(pending, _image);
    }

    private Task? PendingImageUseTask()
    {
        var tasks = new List<Task>(3);
        if (_thumbnailTask is not null) tasks.Add(_thumbnailTask);
        if (_releaseAfterTask is not null) tasks.Add(_releaseAfterTask);
        if (_copyTask is { IsCompleted: false } copyTask) tasks.Add(copyTask);

        return tasks.Count switch
        {
            0 => null,
            1 => tasks[0],
            _ => Task.WhenAll(tasks),
        };
    }

    private static async Task DisposeImageAfterAsync(Task pending, SD.Bitmap image)
    {
        try { await pending.ConfigureAwait(false); }
        catch { }
        image.Dispose();
        MemoryCleanup.Request();
    }

    private void SetThumbnailPlaceholder(SD.Bitmap image)
    {
        var size = CaptureService.GetBitmapSize(image);
        double scale = Math.Min(1.0, Math.Min(320.0 / size.Width, 180.0 / size.Height));
        Thumb.Width = Math.Max(1, Math.Round(size.Width * scale));
        Thumb.Height = Math.Max(1, Math.Round(size.Height * scale));
    }

    private async Task LoadThumbnailAsync(SD.Bitmap image)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var source = await CaptureService.ToBitmapSourceSnapshotAsync(image, 320, 180);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_closed)
                    Thumb.Source = source;
            });
            Log.Info($"Perf quick actions thumbnail ready: {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load overlay thumbnail", ex);
        }
    }

    private async Task WatchHistoryPathAsync(Task<string?> task)
    {
        try
        {
            string? path = await task.ConfigureAwait(false);
            if (path is null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                _historyPath = path;
                if (_closed)
                    PushRecentlyClosed(path);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Failed to attach history path to overlay", ex);
        }
    }

    private static void PushRecentlyClosed(string path)
    {
        lock (RecentlyClosed)
            RecentlyClosed.Push(path);
    }

    /// <summary>Most recent history path of a closed overlay whose file still
    /// exists on disk, or null when there is none.</summary>
    public static string? PopRecentlyClosed()
    {
        lock (RecentlyClosed)
        {
            while (RecentlyClosed.Count > 0)
            {
                string path = RecentlyClosed.Pop();
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private void PositionBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        double offset = OpenWindows
            .Where(w => !ReferenceEquals(w, this) && w.IsVisible)
            .Sum(w => w.ActualHeight + 12);
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16 - offset;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.C: OnCopy(BtnCopy, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S: OnSave(BtnSave, new RoutedEventArgs()); e.Handled = true; break;
            case Key.E: EditRequested?.Invoke(this); e.Handled = true; break;
            case Key.P: PinRequested?.Invoke(this); e.Handled = true; break;
            case Key.O: OcrRequested?.Invoke(this); e.Handled = true; break;
            case Key.B: BackgroundRequested?.Invoke(this); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    // ---- Drag-out from the thumbnail ----

    private void OnThumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = true;
        _dragStart = e.GetPosition(this);
        Thumb.CaptureMouse();
        e.Handled = true; // keep the panel-chrome DragMove from hijacking the gesture
    }

    private async void OnThumbMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        Point pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        Thumb.ReleaseMouseCapture(); // DoDragDrop manages its own capture
        try
        {
            string path = await EnsureDragFileAsync();
            if (_closed || !IsVisible) return;

            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(Thumb, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            _dragFileTask = null;
            Log.Error("Thumbnail drag-out failed", ex);
        }
    }

    private void OnThumbMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (Thumb.IsMouseCaptured) Thumb.ReleaseMouseCapture();
    }

    /// <summary>Returns a file on disk representing this capture: the history
    /// file when available, otherwise a temp PNG written once and reused.</summary>
    private Task<string> EnsureDragFileAsync()
    {
        if (_historyPath is not null && File.Exists(_historyPath))
            return Task.FromResult(_historyPath);
        if (_tempDragPath is not null && File.Exists(_tempDragPath))
            return Task.FromResult(_tempDragPath);

        string dir = Path.Combine(Path.GetTempPath(), "WinShot");
        string path = FileNamer.NextUniquePath(_settings, dir, "png");
        _dragFileTask ??= CreateDragFileAsync(dir, path);
        return _dragFileTask;
    }

    private async Task<string> CreateDragFileAsync(string dir, string path)
    {
        var copy = CaptureService.CloneBitmap(_image);
        await Task.Run(() =>
        {
            using (copy)
            {
                Directory.CreateDirectory(dir);
                ImageSaver.Save(copy, path);
            }
        });
        _tempDragPath = path;
        return path;
    }

    // ---- Panel chrome / buttons ----

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* button released mid-call */ }
        }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            _copyTask = CaptureService.CopyToClipboardAsync(_image);
            await _copyTask;
            if (!IsVisible) return;
            // Glyph button: flash a checkmark (), then restore the copy glyph ().
            BtnCopy.Content = "";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); BtnCopy.Content = ""; };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            var dialog = new SaveFileDialog
            {
                FileName = FileNamer.Next(_settings, _settings.Current.ImageFormat),
                InitialDirectory = _settings.Current.SaveFolder,
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
                FilterIndex = _settings.Current.ImageFormat switch
                {
                    "jpg" => 2,
                    "webp" => 3,
                    _ => 1,
                },
            };
            if (dialog.ShowDialog() == true)
            {
                var copy = CaptureService.CloneBitmap(_image);
                await Task.Run(() =>
                {
                    using (copy)
                        ImageSaver.Save(copy, dialog.FileName);
                });
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
        }
    }

    private void OnEdit(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this);
    private void OnPin(object sender, RoutedEventArgs e) => PinRequested?.Invoke(this);
    private void OnOcr(object sender, RoutedEventArgs e) => OcrRequested?.Invoke(this);
    private void OnBackground(object sender, RoutedEventArgs e) => BackgroundRequested?.Invoke(this);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
