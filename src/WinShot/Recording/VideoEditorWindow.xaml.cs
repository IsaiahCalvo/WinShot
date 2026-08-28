using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinShot.Core;

namespace WinShot.Recording;

/// <summary>
/// Lightweight MP4 editor: preview with play/pause and a position slider,
/// trim start/end, resolution (100/75/50 %), quality (High/Medium/Low), FPS,
/// mute/mono/volume. Export renders through Windows.Media.Editing.MediaComposition
/// to "&lt;original&gt; (edited).mp4" next to the source file, adds it to
/// history, and shows a completion toast.
/// </summary>
public partial class VideoEditorWindow : Window
{
    private readonly string _mp4Path;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private double _durationSec;
    private bool _trimInitialized; // trim range is set up once, not on preview reopen
    private bool _playing;
    private bool _syncingPosition; // true while the timer drives the position slider
    private bool _exporting;
    private bool _closed;
    private bool _exportCancellationRequested;
    private bool _exportCompleted;
    private string? _pendingOutputPath;
    private IAsyncOperationWithProgress<TranscodeFailureReason, double>? _activeRender;

    // Filmstrip-timeline trim state (seconds). Replaces the old trim sliders;
    // every edit is run through VideoTrimRange so clamping stays centralized.
    private double _trimStartSec;
    private double _trimEndSec;
    private bool _filmstripBuilt; // thumbnails are generated at most once

    // Drag handles are 12px wide; the accent bar sits on their inner edge so the
    // selection spans from the start-thumb's right edge to the end-thumb's left.
    private const double ThumbWidth = 12;

    public VideoEditorWindow(string mp4Path, SettingsService settings, HistoryService history)
    {
        InitializeComponent();
        _mp4Path = mp4Path;
        _settings = settings;
        _history = history;
        Title = $"WinShot — Edit {Path.GetFileName(mp4Path)}";
        UpdateOutputSummary();

        _positionTimer.Tick += OnPositionTimer;
        Closed += (_, _) =>
        {
            _closed = true;
            _positionTimer.Stop();
            RequestExportCancellation();
            if (_exporting && !_exportCompleted && _pendingOutputPath is not null)
                _ = DeletePartialOutputAndLogAsync(_pendingOutputPath);
            try { Media.Close(); }
            catch (Exception ex) { Log.Error("Failed to close media preview", ex); }
            MemoryCleanup.Request();
        };

        StatusText.Text = "Loading preview...";
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(OpenPreviewSafely));
        DarkTitleBar.Apply(this);
    }

    /// <summary>Loads (or reloads) the source into the preview and shows the first frame.</summary>
    private void OpenPreview()
    {
        Media.Source = new Uri(_mp4Path);
        Media.Volume = VolumeSlider?.Value ?? 1.0;
        Media.IsMuted = MuteCheck?.IsChecked == true;
        // Play+Pause forces MediaElement to render the first frame instead of staying black.
        Media.Play();
        Media.Pause();
        _playing = false;
        BtnPlay.Content = "_Play";
        _positionTimer.Stop();
    }

    private void OpenPreviewSafely()
    {
        if (_closed)
            return;

        try { OpenPreview(); }
        catch (Exception ex)
        {
            Log.Error("Failed to open media preview", ex);
            StatusText.Text = "Preview failed - the file can still be exported.";
        }
    }

    // ---- preview ----

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
            _durationSec = Media.NaturalDuration.TimeSpan.TotalSeconds;
        StatusText.Text = "";
        PositionSlider.Maximum = Math.Max(0.1, _durationSec);
        if (!_trimInitialized)
        {
            _trimInitialized = true;
            _trimStartSec = 0;
            _trimEndSec = Math.Max(0.1, _durationSec);
            UpdateTrimUi();
            _ = BuildFilmstripAsync();
        }
        UpdateOutputSummary();
        UpdateTimeLabel();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        BtnPlay.Content = "_Play";
        _positionTimer.Stop();
        UpdateTimeLabel();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Error($"Video preview failed for {_mp4Path}", e.ErrorException);
        StatusText.Text = "Preview failed — the file can still be exported.";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        if (_playing)
        {
            Media.Pause();
            _playing = false;
            BtnPlay.Content = "_Play";
            _positionTimer.Stop();
        }
        else
        {
            if (_durationSec > 0 && Media.Position.TotalSeconds >= _durationSec - 0.05)
                Media.Position = TimeSpan.Zero;
            Media.Play();
            _playing = true;
            BtnPlay.Content = "_Pause";
            _positionTimer.Start();
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _exporting)
        {
            CancelExport();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_exporting)
                OnExport(BtnExport, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase &&
            Keyboard.FocusedElement is not Slider &&
            Keyboard.FocusedElement is not ComboBox &&
            Keyboard.FocusedElement is not System.Windows.Controls.Primitives.Thumb)
        {
            OnPlayPause(BtnPlay, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnPositionTimer(object? sender, EventArgs e)
    {
        _syncingPosition = true;
        PositionSlider.Value = Media.Position.TotalSeconds;
        _syncingPosition = false;
        UpdatePlayhead();
        UpdateTimeLabel();
    }

    private void OnPositionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPosition || _exporting) return;
        Media.Position = TimeSpan.FromSeconds(PositionSlider.Value);
        UpdatePlayhead();
        UpdateTimeLabel();
    }

    private void UpdateTimeLabel() =>
        TimeLabel.Text = $"{FormatTime(Media.Position.TotalSeconds)} / {FormatTime(_durationSec)}";

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---- filmstrip trim timeline ----

    /// <summary>Usable horizontal span of the track (px) the playhead and thumbs map onto.</summary>
    private double TimelineSpan => Math.Max(1, TimelineTrack.ActualWidth - ThumbWidth * 2);

    /// <summary>Maps a position in seconds to an x offset (px) on the timeline canvas.</summary>
    private double SecondsToX(double seconds)
    {
        double duration = Math.Max(0.1, _durationSec);
        double fraction = Math.Clamp(seconds / duration, 0, 1);
        return ThumbWidth + fraction * TimelineSpan;
    }

    /// <summary>Maps an x offset (px) on the timeline back to seconds.</summary>
    private double XToSeconds(double x)
    {
        double fraction = Math.Clamp((x - ThumbWidth) / TimelineSpan, 0, 1);
        return fraction * Math.Max(0.1, _durationSec);
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimUi();

    private void OnTimelineClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_exporting || _durationSec <= 0) return;
        // Clicking the empty track scrubs the preview (handles capture their own drags).
        if (e.OriginalSource is FrameworkElement fe &&
            (ReferenceEquals(fe, TrimStartThumb) || ReferenceEquals(fe, TrimEndThumb)))
            return;
        double x = e.GetPosition(TimelineCanvas).X;
        double seconds = XToSeconds(x);
        Media.Position = TimeSpan.FromSeconds(seconds);
        _syncingPosition = true;
        PositionSlider.Value = seconds;
        _syncingPosition = false;
        UpdatePlayhead();
        UpdateTimeLabel();
    }

    private void OnTrimStartDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_durationSec <= 0) return;
        double newX = Canvas.GetLeft(TrimStartThumb) + ThumbWidth + e.HorizontalChange;
        double requested = XToSeconds(newX);
        var range = VideoEditorInteraction.AdjustTrimStart(
            _trimStartSec, _trimEndSec, _durationSec, requested - _trimStartSec);
        _trimStartSec = range.StartSeconds;
        _trimEndSec = range.EndSeconds;
        UpdateTrimUi();
    }

    private void OnTrimEndDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_durationSec <= 0) return;
        double newX = Canvas.GetLeft(TrimEndThumb) + e.HorizontalChange;
        double requested = XToSeconds(newX);
        var range = VideoEditorInteraction.AdjustTrimEnd(
            _trimStartSec, _trimEndSec, _durationSec, requested - _trimEndSec);
        _trimStartSec = range.StartSeconds;
        _trimEndSec = range.EndSeconds;
        UpdateTrimUi();
    }

    private void OnTrimStartKeyDown(object sender, KeyEventArgs e)
    {
        if (_exporting || _durationSec <= 0) return;

        VideoTrimRange range;
        if (e.Key == Key.Home)
        {
            range = VideoTrimRange.FromStart(0, _trimEndSec, _durationSec);
        }
        else if (e.Key == Key.End)
        {
            range = VideoTrimRange.FromStart(
                _trimEndSec - VideoTrimRange.MinimumDurationSeconds,
                _trimEndSec,
                _durationSec);
        }
        else if (TryGetTrimDirection(e.Key, out double direction))
        {
            double step = VideoEditorInteraction.TrimStepSeconds(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            range = VideoEditorInteraction.AdjustTrimStart(
                _trimStartSec, _trimEndSec, _durationSec, direction * step);
        }
        else
        {
            return;
        }

        ApplyTrimRange(range, range.StartSeconds);
        e.Handled = true;
    }

    private void OnTrimEndKeyDown(object sender, KeyEventArgs e)
    {
        if (_exporting || _durationSec <= 0) return;

        VideoTrimRange range;
        if (e.Key == Key.Home)
        {
            range = VideoTrimRange.FromEnd(
                _trimStartSec,
                _trimStartSec + VideoTrimRange.MinimumDurationSeconds,
                _durationSec);
        }
        else if (e.Key == Key.End)
        {
            range = VideoTrimRange.FromEnd(_trimStartSec, _durationSec, _durationSec);
        }
        else if (TryGetTrimDirection(e.Key, out double direction))
        {
            double step = VideoEditorInteraction.TrimStepSeconds(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            range = VideoEditorInteraction.AdjustTrimEnd(
                _trimStartSec, _trimEndSec, _durationSec, direction * step);
        }
        else
        {
            return;
        }

        ApplyTrimRange(range, range.EndSeconds);
        e.Handled = true;
    }

    private static bool TryGetTrimDirection(Key key, out double direction)
    {
        direction = key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            _ => 0,
        };
        return direction != 0;
    }

    private void ApplyTrimRange(VideoTrimRange range, double previewSeconds)
    {
        _trimStartSec = range.StartSeconds;
        _trimEndSec = range.EndSeconds;
        Media.Position = TimeSpan.FromSeconds(previewSeconds);
        _syncingPosition = true;
        PositionSlider.Value = previewSeconds;
        _syncingPosition = false;
        UpdateTrimUi();
        UpdateTimeLabel();
    }

    /// <summary>Repositions the thumbs, dimmers, selection border, and labels to match the trim range.</summary>
    private void UpdateTrimUi()
    {
        if (TimelineTrack is null || TrimStartThumb is null || TrimEndThumb is null)
            return; // XAML init order

        double startX = SecondsToX(_trimStartSec); // inner edge of the kept region
        double endX = SecondsToX(_trimEndSec);
        double height = Math.Max(0, TimelineTrack.ActualHeight - 2);

        // Thumbs are anchored so their accent bar sits on the selection boundary.
        Canvas.SetLeft(TrimStartThumb, startX - ThumbWidth);
        Canvas.SetLeft(TrimEndThumb, endX);
        TrimStartThumb.Height = height;
        TrimEndThumb.Height = height;

        LeftDim.Width = Math.Max(0, startX);
        LeftDim.Height = height;
        Canvas.SetLeft(RightDim, endX);
        RightDim.Width = Math.Max(0, TimelineCanvasWidth() - endX);
        RightDim.Height = height;

        Canvas.SetLeft(SelectionBorder, startX);
        SelectionBorder.Width = Math.Max(0, endX - startX);
        SelectionBorder.Height = height;

        if (TrimStartLabel is not null) TrimStartLabel.Text = $"In {FormatTime(_trimStartSec)}";
        if (TrimEndLabel is not null) TrimEndLabel.Text = $"Out {FormatTime(_trimEndSec)}";
        if (TrimRangeLabel is not null) TrimRangeLabel.Text = FormatTime(Math.Max(0, _trimEndSec - _trimStartSec));

        UpdatePlayhead();
    }

    private double TimelineCanvasWidth() => Math.Max(0, TimelineTrack.ActualWidth);

    private void UpdatePlayhead()
    {
        if (Playhead is null || TimelineTrack is null) return;
        Canvas.SetLeft(Playhead, SecondsToX(Media.Position.TotalSeconds) - 1);
        Playhead.Height = Math.Max(0, TimelineTrack.ActualHeight - 4);
    }

    /// <summary>
    /// Best-effort filmstrip: renders a handful of evenly spaced frames from the
    /// MediaComposition and lays them across the track. On any failure the track
    /// stays a plain dark strip — the trim handles work regardless.
    /// </summary>
    private async Task BuildFilmstripAsync()
    {
        if (_filmstripBuilt || _durationSec <= 0) return;
        _filmstripBuilt = true;

        const int frameCount = 8;
        const int thumbHeight = 54;
        try
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(_mp4Path);
            var clip = await MediaClip.CreateFromFileAsync(srcFile);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var props = clip.GetVideoEncodingProperties();
            double aspect = props.Width > 0 && props.Height > 0
                ? (double)props.Width / props.Height
                : 16.0 / 9.0;
            int thumbWidth = Math.Max(1, (int)Math.Round(thumbHeight * aspect));

            var images = new List<System.Windows.Media.Imaging.BitmapImage?>();
            for (int i = 0; i < frameCount; i++)
            {
                if (_closed) return;
                double t = _durationSec * (i + 0.5) / frameCount;
                var time = TimeSpan.FromSeconds(Math.Clamp(t, 0, Math.Max(0, _durationSec - 0.01)));
                images.Add(await TryGetThumbnailAsync(composition, time, thumbWidth, thumbHeight));
            }

            if (_closed) return;
            RenderFilmstrip(images, thumbWidth, thumbHeight);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to build filmstrip thumbnails; using a plain track", ex);
        }
    }

    private static async Task<System.Windows.Media.Imaging.BitmapImage?> TryGetThumbnailAsync(
        MediaComposition composition, TimeSpan time, int width, int height)
    {
        try
        {
            // NearestKeyFrame is cheap to decode — good enough for a scrub strip.
            var stream = await composition.GetThumbnailAsync(
                time, width, height, VideoFramePrecision.NearestKeyFrame);
            if (stream is null) return null;

            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to render a filmstrip frame", ex);
            return null;
        }
    }

    private void RenderFilmstrip(
        IReadOnlyList<System.Windows.Media.Imaging.BitmapImage?> images, int thumbWidth, int thumbHeight)
    {
        FilmstripItems.Items.Clear();
        if (images.Count == 0) return;

        // Tile thumbnails left-to-right across the full track width.
        double span = Math.Max(thumbWidth, TimelineTrack.ActualWidth);
        double step = span / images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i] is null) continue;
            var image = new System.Windows.Controls.Image
            {
                Source = images[i],
                Width = step + 1, // +1 avoids hairline gaps between tiles
                Height = thumbHeight,
                Stretch = System.Windows.Media.Stretch.UniformToFill,
            };
            Canvas.SetLeft(image, i * step);
            Canvas.SetTop(image, 0);
            FilmstripItems.Items.Add(image);
        }
    }

    // ---- audio controls ----

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        if (Media is not null)
            Media.IsMuted = MuteCheck.IsChecked == true;
        UpdateOutputSummary();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null) return;
        Media.Volume = VolumeSlider.Value;
    }

    private void OnOutputSettingsChanged(object sender, RoutedEventArgs e) => UpdateOutputSummary();

    private void UpdateOutputSummary()
    {
        if (OutputSummaryText is null || ResolutionCombo is null || QualityCombo is null ||
            FrameRateCombo is null || MuteCheck is null || MonoCheck is null)
            return;

        string dimensions = "Source dimensions";
        if (Media is not null && Media.NaturalVideoWidth > 0 && Media.NaturalVideoHeight > 0)
        {
            var video = VideoExportVideoSettings.FromControls(
                (uint)Media.NaturalVideoWidth,
                (uint)Media.NaturalVideoHeight,
                sourceFrameRate: 30,
                ResolutionCombo.SelectedIndex,
                QualityCombo.SelectedIndex,
                FrameRateCombo.SelectedIndex);
            dimensions = $"{video.Width} × {video.Height}";
        }

        string quality = SelectedComboText(QualityCombo, "High");
        string frameRate = SelectedComboText(FrameRateCombo, "Source") + " FPS";
        string audio = MuteCheck.IsChecked == true
            ? "Mute audio"
            : MonoCheck.IsChecked == true ? "Mono audio" : "Original audio";
        OutputSummaryText.Text = $"{dimensions}  •  {quality}  •  {frameRate}  •  {audio}";
    }

    private static string SelectedComboText(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    // ---- export ----

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        if (_durationSec <= 0)
        {
            StatusText.Text = "Video is still loading — try again in a moment.";
            return;
        }
        var trimRange = VideoTrimRange.Normalize(_trimStartSec, _trimEndSec, _durationSec);
        if (!trimRange.IsExportable)
        {
            StatusText.Text = "Trim range is empty — nothing to export.";
            return;
        }

        double startSec = trimRange.StartSeconds;

        _exporting = true;
        _exportCancellationRequested = false;
        _exportCompleted = false;
        _pendingOutputPath = null;
        SetExportUiState(exporting: true);
        ExportProgress.Value = 0;
        StatusText.Text = "Preparing export…";
        if (_playing)
        {
            Media.Pause();
            _playing = false;
            BtnPlay.Content = "_Play";
            _positionTimer.Stop();
        }
        // Release the preview's handle on the source while the render reads it.
        try { Media.Close(); }
        catch (Exception ex) { Log.Error("Failed to close preview before export", ex); }

        try
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(_mp4Path);
            ThrowIfExportCanceled();
            var clip = await MediaClip.CreateFromFileAsync(srcFile);
            ThrowIfExportCanceled();
            clip.TrimTimeFromStart = TimeSpan.FromSeconds(startSec);
            clip.TrimTimeFromEnd = TimeSpan.FromSeconds(trimRange.TrimFromEndSeconds(_durationSec));
            var audioSettings = VideoExportAudioSettings.FromControls(
                MuteCheck.IsChecked == true,
                VolumeSlider.Value,
                MonoCheck.IsChecked == true);
            clip.Volume = audioSettings.ClipVolume;

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var props = clip.GetVideoEncodingProperties();
            uint srcW = props.Width;
            uint srcH = props.Height;
            if (srcW == 0 || srcH == 0)
            {
                srcW = 1920;
                srcH = 1080;
            }
            double sourceFps = props.FrameRate is { Denominator: > 0 } rate
                ? (double)rate.Numerator / rate.Denominator
                : 30;
            var videoSettings = VideoExportVideoSettings.FromControls(
                srcW,
                srcH,
                sourceFps,
                ResolutionCombo.SelectedIndex,
                QualityCombo.SelectedIndex,
                FrameRateCombo.SelectedIndex);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = videoSettings.Width;
            profile.Video.Height = videoSettings.Height;
            profile.Video.Bitrate = videoSettings.Bitrate;
            profile.Video.FrameRate.Numerator = (uint)Math.Round(videoSettings.FrameRate);
            profile.Video.FrameRate.Denominator = 1;
            if (audioSettings.OutputChannelCount is uint channelCount && profile.Audio is not null)
                profile.Audio.ChannelCount = channelCount;

            string dir = Path.GetDirectoryName(_mp4Path)!;
            string baseName = Path.GetFileNameWithoutExtension(_mp4Path);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            ThrowIfExportCanceled();
            var outFile = await folder.CreateFileAsync($"{baseName} (edited).mp4", CreationCollisionOption.GenerateUniqueName);
            _pendingOutputPath = outFile.Path;
            ThrowIfExportCanceled();

            var render = composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            _activeRender = render;
            if (_exportCancellationRequested)
                render.Cancel();
            render.Progress = (operation, progress) =>
            {
                if (_closed) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed || !_exporting) return;
                    double safeProgress = Math.Clamp(progress, 0, 100);
                    ExportProgress.Value = safeProgress;
                    StatusText.Text = $"Exporting… {Math.Round(safeProgress):0}%";
                }));
            };
            var result = await render;
            ThrowIfExportCanceled();
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Render failed: {result}");

            string savedPath = outFile.Path;
            _exportCompleted = true;
            _ = Task.Run(() =>
            {
                try { _history.AddFile(savedPath); }
                catch (Exception ex) { Log.Error("Failed to add edited video to history", ex); }
            });

            if (_closed)
                return;

            StatusText.Text = $"Saved {outFile.Name}";
            Log.Info($"Edited video exported to {savedPath}");
            RecordingCompletionOverlay.Show(
                savedPath,
                _settings,
                onEdit: () => new VideoEditorWindow(savedPath, _settings, _history).Show());
        }
        catch (Exception ex) when (VideoEditorInteraction.IsCancellation(ex) || _exportCancellationRequested)
        {
            string? partialPath = _pendingOutputPath;
            bool removed = await DeletePartialOutputAndLogAsync(partialPath);
            if (!_closed)
            {
                StatusText.Text = removed
                    ? "Export canceled. No partial file was kept."
                    : $"Export canceled, but WinShot could not remove {Path.GetFileName(partialPath)}. Delete that partial file before retrying.";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Video export failed for {_mp4Path}", ex);
            string? partialPath = _pendingOutputPath;
            bool removed = await DeletePartialOutputAndLogAsync(partialPath);
            if (!_closed)
            {
                string message = VideoEditorInteraction.ExportFailureMessage(ex);
                if (!removed && partialPath is not null)
                    message += $" WinShot could not remove the partial file: {partialPath}";
                StatusText.Text = "Export failed. Review the message and try again.";
                MessageBox.Show(this, message, "WinShot export", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _activeRender = null;
            _exporting = false;
            _pendingOutputPath = null;
            if (!_closed)
            {
                SetExportUiState(exporting: false);
                OpenPreviewSafely();
            }
        }
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => CancelExport();

    private void OnCloseEditor(object sender, RoutedEventArgs e)
    {
        if (!_exporting)
            Close();
    }

    private void CancelExport()
    {
        if (!_exporting || _exportCancellationRequested)
            return;

        _exportCancellationRequested = true;
        BtnCancelExport.IsEnabled = false;
        StatusText.Text = "Canceling export…";
        RequestExportCancellation();
    }

    private void RequestExportCancellation()
    {
        if (!_exporting)
            return;

        _exportCancellationRequested = true;
        try { _activeRender?.Cancel(); }
        catch (Exception ex) { Log.Error("Failed to request video export cancellation", ex); }
    }

    private void ThrowIfExportCanceled()
    {
        if (_exportCancellationRequested || _closed)
            throw new OperationCanceledException("Video export canceled.");
    }

    private void SetExportUiState(bool exporting)
    {
        BtnExport.IsEnabled = !exporting;
        BtnClose.Visibility = exporting ? Visibility.Collapsed : Visibility.Visible;
        BtnCancelExport.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelExport.IsEnabled = exporting;
        ExportProgress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;

        BtnPlay.IsEnabled = !exporting;
        PositionSlider.IsEnabled = !exporting;
        TimelineTrack.IsEnabled = !exporting;
        ResolutionCombo.IsEnabled = !exporting;
        QualityCombo.IsEnabled = !exporting;
        FrameRateCombo.IsEnabled = !exporting;
        MuteCheck.IsEnabled = !exporting;
        MonoCheck.IsEnabled = !exporting;
        VolumeSlider.IsEnabled = !exporting;
    }

    private static async Task<bool> DeletePartialOutputAndLogAsync(string? partialPath)
    {
        bool removed = await VideoEditorInteraction.TryDeletePartialOutputAsync(partialPath);
        if (!removed && partialPath is not null)
            Log.Info($"Could not delete partial video export: {partialPath}");
        return removed;
    }
}
