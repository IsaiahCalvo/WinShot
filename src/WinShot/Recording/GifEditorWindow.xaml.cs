using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// A recorded GIF could not be edited at all: the video editor is a MediaElement, and the
/// after-actions forced its editor off for GIFs, so the only way to shorten one was to record
/// it again. This is the smallest thing that fixes that — the same filmstrip idiom as the video
/// editor, driven by frame index rather than seconds, saving through the encoder that wrote the
/// file in the first place.
/// </summary>
public partial class GifEditorWindow : Window
{
    private const double ThumbWidth = 12;
    private const int FilmstripFrames = 8;
    private const int ThumbHeight = 54;

    private readonly string _gifPath;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly DispatcherTimer _playback = new();
    private readonly Dictionary<int, BitmapSource> _decoded = new();

    private GifInfo _info;
    private int _first;
    private int _last;
    private int _current;
    private bool _playing;
    private bool _scrubbing;
    private bool _closed;
    private bool _saving;
    private IReadOnlyList<BitmapSource?> _filmstrip = Array.Empty<BitmapSource?>();

    public GifEditorWindow(string gifPath, SettingsService settings, HistoryService history)
    {
        InitializeComponent();
        _gifPath = gifPath;
        _settings = settings;
        _history = history;
        Title = $"WinShot — Trim {Path.GetFileName(gifPath)}";

        _playback.Tick += (_, _) => Step();
        Closed += (_, _) =>
        {
            _closed = true;
            _playback.Stop();
            _decoded.Clear();
            MemoryCleanup.Request();
        };

        StatusText.Text = "Loading frames…";
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Load));
        DarkTitleBar.Apply(this);
    }

    private void Load()
    {
        if (_closed)
            return;

        try
        {
            _info = GifEditing.Read(_gifPath);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read {_gifPath}", ex);
            StatusText.Text = "This GIF could not be read.";
            BtnSave.IsEnabled = false;
            return;
        }

        if (!_info.IsUsable)
        {
            StatusText.Text = "This GIF has no frames to trim.";
            BtnSave.IsEnabled = false;
            return;
        }

        _first = 0;
        _last = _info.FrameCount - 1;
        _current = 0;
        _playback.Interval = TimeSpan.FromSeconds(_info.FrameSeconds);
        StatusText.Text = "";
        BuildFilmstrip();
        ShowFrame(0);
        UpdateTrimUi();
    }

    // ---- preview -----------------------------------------------------------------

    private BitmapSource? Frame(int index)
    {
        if (_decoded.TryGetValue(index, out BitmapSource? cached))
            return cached;

        using SD.Bitmap? bitmap = GifEditing.ReadFrame(_gifPath, index);
        if (bitmap is null)
            return null;

        BitmapSource source = ToBitmapSource(bitmap);
        // A long GIF decoded frame by frame would otherwise hold every frame at full size.
        if (_decoded.Count > 64)
            _decoded.Clear();
        _decoded[index] = source;
        return source;
    }

    private static BitmapSource ToBitmapSource(SD.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, SD.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ShowFrame(int index)
    {
        if (!_info.IsUsable)
            return;

        _current = Math.Clamp(index, 0, _info.FrameCount - 1);
        Preview.Source = Frame(_current);
        FrameLabel.Text = $"{_current + 1} / {_info.FrameCount}";
        UpdatePlayhead();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (!_info.IsUsable || _saving)
            return;

        _playing = !_playing;
        BtnPlay.Content = _playing ? "Pause" : "Play";
        if (_playing)
        {
            if (_current >= _last)
                ShowFrame(_first);
            _playback.Start();
        }
        else
        {
            _playback.Stop();
        }
    }

    /// <summary>Playback loops the kept range, so what you see is what you are about to save.</summary>
    private void Step() => ShowFrame(_current >= _last ? _first : _current + 1);

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnSave(BtnSave, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && !_saving)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.Thumb)
        {
            OnPlayPause(BtnPlay, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---- filmstrip ---------------------------------------------------------------

    private void BuildFilmstrip()
    {
        var frames = new List<BitmapSource?>(FilmstripFrames);
        for (int i = 0; i < FilmstripFrames; i++)
        {
            int index = (int)Math.Round((i + 0.5) * (_info.FrameCount - 1) / FilmstripFrames);
            frames.Add(Frame(Math.Clamp(index, 0, _info.FrameCount - 1)));
        }
        _filmstrip = frames;
        LayOutFilmstrip();
    }

    private void LayOutFilmstrip()
    {
        if (FilmstripItems is null || TimelineTrack is null || _filmstrip.Count == 0)
            return;

        FilmstripItems.Items.Clear();
        double span = Math.Max(1, TimelineTrack.ActualWidth);
        double step = span / _filmstrip.Count;
        for (int i = 0; i < _filmstrip.Count; i++)
        {
            if (_filmstrip[i] is null)
                continue;
            var image = new Image
            {
                Source = _filmstrip[i],
                Width = step + 1, // +1 avoids hairline gaps between tiles
                Height = ThumbHeight,
                Stretch = System.Windows.Media.Stretch.UniformToFill,
            };
            Canvas.SetLeft(image, i * step);
            Canvas.SetTop(image, 0);
            FilmstripItems.Items.Add(image);
        }
    }

    // ---- timeline ----------------------------------------------------------------

    private double TimelineSpan => Math.Max(1, TimelineTrack.ActualWidth - (ThumbWidth * 2));

    private double FrameToX(int frame)
    {
        double fraction = _info.FrameCount <= 1
            ? 0
            : Math.Clamp(frame / (double)(_info.FrameCount - 1), 0, 1);
        return ThumbWidth + (fraction * TimelineSpan);
    }

    private int XToFrame(double x)
    {
        double fraction = Math.Clamp((x - ThumbWidth) / TimelineSpan, 0, 1);
        return (int)Math.Round(fraction * Math.Max(0, _info.FrameCount - 1));
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayOutFilmstrip();
        UpdateTrimUi();
    }

    private void OnTimelineClicked(object sender, MouseButtonEventArgs e)
    {
        if (_saving || !_info.IsUsable) return;
        if (e.OriginalSource is FrameworkElement fe &&
            (ReferenceEquals(fe, TrimStartThumb) || ReferenceEquals(fe, TrimEndThumb)))
            return;

        _scrubbing = true;
        TimelineTrack.CaptureMouse();
        ShowFrame(XToFrame(e.GetPosition(TimelineCanvas).X));
    }

    private void OnTimelineDragScrub(object sender, MouseEventArgs e)
    {
        if (!_scrubbing || _saving) return;
        ShowFrame(XToFrame(e.GetPosition(TimelineCanvas).X));
    }

    private void OnTimelineScrubReleased(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        if (TimelineTrack.IsMouseCaptured)
            TimelineTrack.ReleaseMouseCapture();
    }

    private void OnTrimStartDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_info.IsUsable) return;
        SetRange(XToFrame(Canvas.GetLeft(TrimStartThumb) + ThumbWidth + e.HorizontalChange), _last, previewFirst: true);
    }

    private void OnTrimEndDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (!_info.IsUsable) return;
        SetRange(_first, XToFrame(Canvas.GetLeft(TrimEndThumb) + e.HorizontalChange), previewFirst: false);
    }

    private void OnTrimStartKeyDown(object sender, KeyEventArgs e)
    {
        if (!TryFrameStep(e, out int step)) return;
        SetRange(_first + step, _last, previewFirst: true);
        e.Handled = true;
    }

    private void OnTrimEndKeyDown(object sender, KeyEventArgs e)
    {
        if (!TryFrameStep(e, out int step)) return;
        SetRange(_first, _last + step, previewFirst: false);
        e.Handled = true;
    }

    private static bool TryFrameStep(KeyEventArgs e, out int step)
    {
        int direction = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        step = direction * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        return direction != 0;
    }

    /// <summary>Applies a new kept range, keeping at least one frame and never inverting.</summary>
    private void SetRange(int first, int last, bool previewFirst)
    {
        if (!_info.IsUsable || _saving)
            return;

        int max = _info.FrameCount - 1;
        _first = Math.Clamp(first, 0, max);
        _last = Math.Clamp(last, 0, max);
        if (_last < _first)
        {
            if (previewFirst)
                _last = _first;
            else
                _first = _last;
        }

        ShowFrame(previewFirst ? _first : _last);
        UpdateTrimUi();
    }

    private void UpdateTrimUi()
    {
        if (TimelineTrack is null || TrimStartThumb is null || TrimEndThumb is null)
            return;

        double startX = FrameToX(_first);
        double endX = FrameToX(_last);
        double height = Math.Max(0, TimelineTrack.ActualHeight - 2);

        Canvas.SetLeft(TrimStartThumb, startX - ThumbWidth);
        Canvas.SetLeft(TrimEndThumb, endX);
        TrimStartThumb.Height = height;
        TrimEndThumb.Height = height;

        LeftDim.Width = Math.Max(0, startX);
        LeftDim.Height = height;
        Canvas.SetLeft(RightDim, endX);
        RightDim.Width = Math.Max(0, TimelineTrack.ActualWidth - endX);
        RightDim.Height = height;

        Canvas.SetLeft(SelectionBorder, startX);
        SelectionBorder.Width = Math.Max(0, endX - startX);
        SelectionBorder.Height = height;

        int kept = GifTrim.FrameCount((_first, _last));
        TrimStartLabel.Text = $"In {_first + 1}";
        TrimEndLabel.Text = $"Out {_last + 1}";
        TrimRangeLabel.Text = kept == 1 ? "1 frame" : $"{kept} frames";
        OutputSummaryText.Text =
            $"{_info.Width} × {_info.Height}  •  {_info.Fps} FPS  •  {kept} of {_info.FrameCount} frames" +
            $"  •  {FormatSeconds(kept * _info.FrameSeconds)}";
        UpdatePlayhead();
    }

    private static string FormatSeconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private void UpdatePlayhead()
    {
        if (Playhead is null || TimelineTrack is null)
            return;
        Canvas.SetLeft(Playhead, FrameToX(_current) - 1);
        Playhead.Height = Math.Max(0, TimelineTrack.ActualHeight - 4);
    }

    // ---- save --------------------------------------------------------------------

    private void OnCloseEditor(object sender, RoutedEventArgs e)
    {
        if (!_saving)
            Close();
    }

    /// <summary>
    /// The encode is already running and its output is already being written, so the window has
    /// to stay up to finish the history entry and the completion card. Close and Escape are
    /// guarded the same way; without this the title-bar X left a trimmed GIF on disk that the
    /// app never told the user about.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_saving)
            e.Cancel = true;
        base.OnClosing(e);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving || !_info.IsUsable)
            return;

        _saving = true;
        _playing = false;
        _playback.Stop();
        BtnPlay.Content = "Play";
        SetSavingUi(true);
        StatusText.Text = "Saving…";

        string source = _gifPath;
        int first = _first;
        int last = _last;
        string destination = Path.Combine(
            Path.GetDirectoryName(source)!,
            VideoExportDestination.EditedFileName(source));
        destination = UniquePath(destination);

        int written = await Task.Run(() => GifEditing.Trim(source, destination, first, last));

        if (_closed)
            return;

        _saving = false;
        SetSavingUi(false);
        if (written <= 0)
        {
            StatusText.Text = "The GIF could not be saved. Nothing was changed.";
            ThemedMessageDialog.Show(
                this,
                "Could not save the GIF",
                "WinShot could not write the trimmed GIF. The original is untouched.");
            return;
        }

        try { _history.AddFile(destination); }
        catch (Exception ex) { Log.Error("Failed to add the trimmed GIF to history", ex); }

        StatusText.Text = $"Saved {Path.GetFileName(destination)} — {written} frames";
        Log.Info($"Trimmed GIF written to {destination} ({written} frames)");
        try
        {
            RecordingCompletionOverlay.Show(
                destination,
                _settings,
                onEdit: () => new GifEditorWindow(destination, _settings, _history).Show());
        }
        catch (Exception ex)
        {
            Log.Error("Failed to show the completion card for a trimmed GIF", ex);
        }
    }

    private void SetSavingUi(bool saving)
    {
        BtnSave.IsEnabled = !saving;
        BtnClose.IsEnabled = !saving;
        BtnPlay.IsEnabled = !saving;
        TimelineTrack.IsEnabled = !saving;
    }

    /// <summary>Never overwrite a neighbour: the same rule the recording finalizer follows.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }
}
