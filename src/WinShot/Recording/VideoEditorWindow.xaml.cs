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
/// trim start/end, resolution (100/75/50 %), quality (High/Medium/Low), and
/// mute/volume. Export renders through Windows.Media.Editing.MediaComposition
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
            _positionTimer.Stop();
            try { Media.Close(); }
            catch (Exception ex) { Log.Error("Failed to close media preview", ex); }
        };

        OpenPreview();
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

    // ---- preview ----

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
            _durationSec = Media.NaturalDuration.TimeSpan.TotalSeconds;
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
        if (TrimStartSlider.Value > TrimEndSlider.Value - 0.1)
            TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 0.1);
        TrimStartLabel.Text = FormatTime(TrimStartSlider.Value);
    }

    private void OnTrimEndChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TrimEndLabel is null || TrimStartSlider is null) return; // XAML init order
        if (TrimEndSlider.Value < TrimStartSlider.Value + 0.1)
            TrimEndSlider.Value = Math.Min(TrimEndSlider.Maximum, TrimStartSlider.Value + 0.1);
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
        double startSec = TrimStartSlider.Value;
        double endSec = TrimEndSlider.Value;
        if (endSec - startSec < 0.1)
        {
            StatusText.Text = "Trim range is empty — nothing to export.";
            return;
        }

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
            clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, _durationSec - endSec));
            clip.Volume = MuteCheck.IsChecked == true ? 0 : VolumeSlider.Value;

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
            double scale = ResolutionCombo.SelectedIndex switch { 1 => 0.75, 2 => 0.5, _ => 1.0 };
            // H.264 wants even dimensions.
            uint w = (uint)Math.Max(2, (int)Math.Round(srcW * scale) & ~1);
            uint h = (uint)Math.Max(2, (int)Math.Round(srcH * scale) & ~1);
            double fps = props.FrameRate is { Denominator: > 0 } rate
                ? (double)rate.Numerator / rate.Denominator
                : 30;
            if (fps <= 0 || fps > 120) fps = 30;
            double bitsPerPixel = QualityCombo.SelectedIndex switch { 1 => 0.08, 2 => 0.045, _ => 0.13 };
            uint bitrate = (uint)Math.Clamp(w * h * fps * bitsPerPixel, 500_000, 60_000_000);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = w;
            profile.Video.Height = h;
            profile.Video.Bitrate = bitrate;

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
            try { _history.AddFile(savedPath); }
            catch (Exception ex) { Log.Error("Failed to add edited video to history", ex); }

            StatusText.Text = $"Saved {outFile.Name}";
            Log.Info($"Edited video exported to {savedPath}");
            new RecordingToastWindow(savedPath,
                onEdit: () => new VideoEditorWindow(savedPath, _settings, _history).Show()).Show();
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
            try { OpenPreview(); }
            catch (Exception ex) { Log.Error("Failed to reopen preview after export", ex); }
        }
    }
}
