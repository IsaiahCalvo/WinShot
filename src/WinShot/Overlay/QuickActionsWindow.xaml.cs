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
    private readonly string? _historyPath;
    private string? _tempDragPath;
    private bool _dragArmed;
    private Point _dragStart;

    public event Action<QuickActionsWindow>? EditRequested;
    public event Action<QuickActionsWindow>? PinRequested;
    public event Action<QuickActionsWindow>? OcrRequested;
    public event Action<QuickActionsWindow>? BackgroundRequested;

    public QuickActionsWindow(SD.Bitmap image, SettingsService settings, string? historyPath = null)
    {
        InitializeComponent();
        _image = image;
        _settings = settings;
        _historyPath = historyPath;
        Thumb.Source = CaptureService.ToBitmapSource(image);

        OpenWindows.Add(this);
        Loaded += (_, _) => PositionBottomRight();
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            if (_historyPath is not null)
            {
                lock (RecentlyClosed)
                    RecentlyClosed.Push(_historyPath);
            }
            _image.Dispose();
        };

        int seconds = settings.Current.OverlayAutoCloseSeconds;
        if (seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    public SD.Bitmap CloneImage() => (SD.Bitmap)_image.Clone();

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

    private void OnThumbMouseMove(object sender, MouseEventArgs e)
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
            string path = EnsureDragFile();
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop(Thumb, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
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
    private string EnsureDragFile()
    {
        if (_historyPath is not null && File.Exists(_historyPath)) return _historyPath;
        if (_tempDragPath is not null && File.Exists(_tempDragPath)) return _tempDragPath;

        string dir = Path.Combine(Path.GetTempPath(), "WinShot");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileNamer.Next(_settings, "png"));
        ImageSaver.Save(_image, path);
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

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            CaptureService.CopyToClipboard(_image);
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

    private void OnSave(object sender, RoutedEventArgs e)
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
                ImageSaver.Save(_image, dialog.FileName);
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
