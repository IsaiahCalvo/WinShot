using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    private bool _trimInitialized; // trim sliders are set up once, not on preview reopen
    private bool _playing;
    private bool _syncingPosition; // true while the timer drives the position slider
    private bool _exporting;
    private bool _closed;

    public VideoEditorWindow(string mp4Path, SettingsService settings, HistoryService history)
    {
        InitializeComponent();
        _mp4Path = mp4Path;
        _settings = settings;
        _history = history;
        Title = $"WinShot — Edit {Path.GetFileName(mp4Path)}";

        _positionTimer.Tick += OnPositionTimer;
        Closed += (_, _) =>
        {
            _closed = true;
            _positionTimer.Stop();
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
        BtnPlay.Content = "Play";
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
            TrimStartSlider.Maximum = Math.Max(0.1, _durationSec);
            TrimEndSlider.Maximum = Math.Max(0.1, _durationSec);
            TrimEndSlider.Value = TrimEndSlider.Maximum;
        }
        UpdateTimeLabel();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        BtnPlay.Content = "Play";
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
            BtnPlay.Content = "Play";
            _positionTimer.Stop();
        }
        else
        {
            if (_durationSec > 0 && Media.Position.TotalSeconds >= _durationSec - 0.05)
                Media.Position = TimeSpan.Zero;
            Media.Play();
            _playing = true;
            BtnPlay.Content = "Pause";
            _positionTimer.Start();
        }
    }

    private void OnPositionTimer(object? sender, EventArgs e)
    {
        _syncingPosition = true;
        PositionSlider.Value = Media.Position.TotalSeconds;
        _syncingPosition = false;
        UpdateTimeLabel();
    }

    private void OnPositionSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingPosition || _exporting) return;
        Media.Position = TimeSpan.FromSeconds(PositionSlider.Value);
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

    // ---- trim / audio controls ----

    private void OnTrimStartChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TrimStartLabel is null || TrimEndSlider is null) return; // XAML init order
        var range = VideoTrimRange.FromStart(TrimStartSlider.Value, TrimEndSlider.Value, _durationSec);
        if (Math.Abs(TrimStartSlider.Value - range.StartSeconds) > 0.001)
            TrimStartSlider.Value = range.StartSeconds;
        if (Math.Abs(TrimEndSlider.Value - range.EndSeconds) > 0.001)
            TrimEndSlider.Value = range.EndSeconds;
        TrimStartLabel.Text = FormatTime(TrimStartSlider.Value);
    }

    private void OnTrimEndChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TrimEndLabel is null || TrimStartSlider is null) return; // XAML init order
        var range = VideoTrimRange.FromEnd(TrimStartSlider.Value, TrimEndSlider.Value, _durationSec);
        if (Math.Abs(TrimStartSlider.Value - range.StartSeconds) > 0.001)
            TrimStartSlider.Value = range.StartSeconds;
        if (Math.Abs(TrimEndSlider.Value - range.EndSeconds) > 0.001)
            TrimEndSlider.Value = range.EndSeconds;
        TrimEndLabel.Text = FormatTime(TrimEndSlider.Value);
    }

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        if (Media is null) return;
        Media.IsMuted = MuteCheck.IsChecked == true;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null) return;
        Media.Volume = VolumeSlider.Value;
    }

    // ---- export ----

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        if (_durationSec <= 0)
        {
            StatusText.Text = "Video is still loading — try again in a moment.";
            return;
        }
        var trimRange = VideoTrimRange.Normalize(TrimStartSlider.Value, TrimEndSlider.Value, _durationSec);
        if (!trimRange.IsExportable)
        {
            StatusText.Text = "Trim range is empty — nothing to export.";
            return;
        }

        double startSec = trimRange.StartSeconds;

        _exporting = true;
        BtnExport.IsEnabled = false;
        ExportProgress.Value = 0;
        ExportProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Exporting…";
        if (_playing)
        {
            Media.Pause();
            _playing = false;
            BtnPlay.Content = "Play";
            _positionTimer.Stop();
        }
        // Release the preview's handle on the source while the render reads it.
        try { Media.Close(); }
        catch (Exception ex) { Log.Error("Failed to close preview before export", ex); }

        try
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(_mp4Path);
            var clip = await MediaClip.CreateFromFileAsync(srcFile);
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
            var outFile = await folder.CreateFileAsync($"{baseName} (edited).mp4", CreationCollisionOption.GenerateUniqueName);

            var render = composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            render.Progress = (_, progress) => Dispatcher.InvokeAsync(() => ExportProgress.Value = progress);
            var result = await render;
            if (result != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Render failed: {result}");

            string savedPath = outFile.Path;
            _ = Task.Run(() =>
            {
                try { _history.AddFile(savedPath); }
                catch (Exception ex) { Log.Error("Failed to add edited video to history", ex); }
            });

            StatusText.Text = $"Saved {outFile.Name}";
            Log.Info($"Edited video exported to {savedPath}");
            var toast = new FastRecordingToastWindow(
                savedPath,
                onEdit: () => new VideoEditorWindow(savedPath, _settings, _history).Show());
            FastRecordingToastWindow.TrackFirstShown(toast, "edited video toast");
            toast.Show();
        }
        catch (Exception ex)
        {
            Log.Error($"Video export failed for {_mp4Path}", ex);
            StatusText.Text = "Export failed — see log for details.";
            MessageBox.Show(this, $"Export failed: {ex.Message}", "WinShot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exporting = false;
            BtnExport.IsEnabled = true;
            ExportProgress.Visibility = Visibility.Collapsed;
            OpenPreviewSafely();
        }
    }
}
