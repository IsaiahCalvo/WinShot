using System.Windows.Threading;
using System.IO;
using ScreenRecorderLib;
using WinShot.Capture;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

/// <summary>
/// Drives the screen-recording flow: options chooser → region pick → optional
/// countdown → MP4 (ScreenRecorderLib) or GIF (timer + streaming encoder),
/// with optional click-highlight and keystroke overlays, pause/resume, then
/// save folder, history, and a completion toast with Open/Reveal/Edit.
/// Construct and call on the UI thread.
/// </summary>
public sealed class RecordingController
{
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly Dispatcher _dispatcher;

    private bool _flowActive;   // chooser/selector currently open
    private bool _stopping;     // stop requested, finalization in flight
    private bool _discard;      // cancel pressed: throw the file away
    private bool _paused;
    private bool _isGif;
    private Recorder? _recorder;
    private GifRecorder? _gifRecorder;
    private RecordingControlBar? _bar;
    private ClickHighlightOverlayWindow? _clickOverlay;
    private KeystrokeOverlayWindow? _keyOverlay;
    private string? _tempPath;

    public RecordingController(SettingsService settings, HistoryService history)
    {
        _settings = settings;
        _history = history;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsRecording { get; private set; }

    /// <summary>Hotkey/tray entry point: starts the recording flow, or stops the active recording.</summary>
    public void ToggleFlow()
    {
        if (IsRecording)
        {
            StopRecording(discard: false);
            return;
        }
        if (_flowActive) return;
        _flowActive = true;
        try
        {
            StartFlow();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            CleanupAfterFailure();
        }
        finally
        {
            _flowActive = false;
        }
    }

    private void StartFlow()
    {
        var dialog = new RecordingOptionsDialog(_settings.Current);
        if (dialog.ShowDialog() != true) return;

        // Remember the choices as new defaults. Deliberately no Save() here —
        // the orchestrating app persists settings (Save re-registers hotkeys).
        var s = _settings.Current;
        s.RecordAudio = dialog.RecordMicrophone;
        s.RecordSystemAudio = dialog.RecordSystemAudio;
        s.CaptureCursor = dialog.CaptureCursor;
        s.ShowClickHighlights = dialog.ShowClickHighlights;
        s.ShowKeystrokes = dialog.ShowKeystrokes;
        s.RecordingCountdownSeconds = dialog.CountdownSeconds;
        s.WebcamOverlayPosition = dialog.WebcamPosition;

        SD.Rectangle screenRect;
        using (var shot = CaptureService.CaptureVirtualDesktop())
        {
            var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows());
            if (selector.ShowDialog() != true || selector.SelectedRegionPx is not SD.Rectangle region)
                return;

            // Selector coordinates are bitmap pixels with origin at the virtual
            // screen top-left; shift into real screen coordinates and round
            // down to even dimensions for H.264.
            var vs = CaptureService.VirtualScreen;
            screenRect = new SD.Rectangle(region.X + vs.X, region.Y + vs.Y, region.Width & ~1, region.Height & ~1);
        }
        if (screenRect.Width < 2 || screenRect.Height < 2)
        {
            Log.Error($"Recording region too small after rounding: {screenRect}");
            return;
        }

        if (dialog.CountdownSeconds > 0)
        {
            var countdown = new RecordingCountdownWindow(dialog.CountdownSeconds, screenRect);
            if (countdown.ShowDialog() != true) return;
        }

        _isGif = dialog.IsGif;
        _discard = false;
        _stopping = false;
        _paused = false;
        string tempDir = Path.Combine(Path.GetTempPath(), "WinShot");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording-{Guid.NewGuid():N}.{(_isGif ? "gif" : "mp4")}");

        // Overlays go up before capture starts so the first frames already
        // have them. Both are click-through and intentionally visible to the
        // capture (rings/pills belong in the output).
        if (dialog.ShowClickHighlights)
        {
            _clickOverlay = new ClickHighlightOverlayWindow(screenRect);
            _clickOverlay.Show();
        }
        if (dialog.ShowKeystrokes)
        {
            _keyOverlay = new KeystrokeOverlayWindow(screenRect);
            _keyOverlay.Show();
        }

        if (_isGif)
            StartGif(screenRect, dialog.CaptureCursor);
        else
            StartMp4(screenRect, dialog.RecordMicrophone, dialog.RecordSystemAudio, dialog.CaptureCursor, dialog.WebcamPosition);

        _bar = new RecordingControlBar();
        _bar.StopRequested += () => StopRecording(discard: false);
        _bar.CancelRequested += () => StopRecording(discard: true);
        _bar.PauseRequested += PauseRecording;
        _bar.ResumeRequested += ResumeRecording;
        _bar.Show();

        IsRecording = true;
        Log.Info($"Recording started ({(_isGif ? "GIF" : "MP4")}) {screenRect}");
    }

    private void StartGif(SD.Rectangle screenRect, bool captureCursor)
    {
        _gifRecorder = new GifRecorder(screenRect, _settings.Current.GifFps, _tempPath!, captureCursor);
        _gifRecorder.Start();
    }

    private void StartMp4(SD.Rectangle screenRect, bool micAudio, bool systemAudio, bool captureCursor, string webcamPosition)
    {
        // ScreenRecorderLib records one display source; clamp the region to the
        // display it overlaps the most. The output crop rect is relative to
        // that display's top-left corner.
        WF.Screen screen = WF.Screen.AllScreens
            .OrderByDescending(s =>
            {
                var overlap = SD.Rectangle.Intersect(s.Bounds, screenRect);
                return (long)overlap.Width * overlap.Height;
            })
            .First();
        var rect = SD.Rectangle.Intersect(screenRect, screen.Bounds);
        rect.Width &= ~1;
        rect.Height &= ~1;
        if (rect.Width < 2 || rect.Height < 2)
            throw new InvalidOperationException("The selected region does not overlap a display.");

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase> { new DisplayRecordingSource(screen.DeviceName) },
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                SourceRect = new ScreenRect(rect.X - screen.Bounds.X, rect.Y - screen.Bounds.Y, rect.Width, rect.Height),
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality },
                Quality = 70,
                Framerate = Math.Clamp(_settings.Current.RecordingFps, 1, 120),
                IsHardwareEncodingEnabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = micAudio || systemAudio,
                IsInputDeviceEnabled = micAudio,
                IsOutputDeviceEnabled = systemAudio,
            },
            // Click feedback comes from our own overlay window (shared with the
            // GIF path); ScreenRecorderLib's built-in click detection stays off.
            MouseOptions = new MouseOptions { IsMousePointerEnabled = captureCursor },
        };

        ApplyWebcamOverlay(options, webcamPosition, rect);

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnMp4Complete;
        _recorder.OnRecordingFailed += OnMp4Failed;
        _recorder.Record(_tempPath!);
    }

    /// <summary>
    /// Adds a webcam picture-in-picture overlay (~22 % of the region width)
    /// anchored at the chosen corner. Skipped quietly when no camera exists.
    /// </summary>
    private static void ApplyWebcamOverlay(RecorderOptions options, string webcamPosition, SD.Rectangle rect)
    {
        if (webcamPosition == "off") return;
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            if (cameras is null || cameras.Count == 0)
            {
                Log.Info("Webcam overlay requested but no video capture device was found");
                return;
            }

            double width = Math.Round(rect.Width * 0.22);
            var overlay = new VideoCaptureOverlay(cameras[0].DeviceName)
            {
                AnchorPoint = webcamPosition switch
                {
                    "top-left" => Anchor.TopLeft,
                    "top-right" => Anchor.TopRight,
                    "bottom-left" => Anchor.BottomLeft,
                    _ => Anchor.BottomRight,
                },
                Offset = new ScreenSize(16, 16),
                Size = new ScreenSize(width, Math.Round(width * 3 / 4)),
                Stretch = StretchMode.Uniform,
            };
            options.OverlayOptions = new OverLayOptions
            {
                Overlays = new List<RecordingOverlayBase> { overlay },
            };
        }
        catch (Exception ex)
        {
            Log.Error("Failed to set up webcam overlay; recording without it", ex);
        }
    }

    private void PauseRecording()
    {
        if (!IsRecording || _stopping || _paused) return;
        _paused = true;
        try
        {
            if (_isGif)
                _gifRecorder?.Pause();
            else
                _recorder?.Pause();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to pause recording", ex);
        }
        _clickOverlay?.SetPaused(true);
        _keyOverlay?.SetPaused(true);
        Log.Info("Recording paused");
    }

    private void ResumeRecording()
    {
        if (!IsRecording || _stopping || !_paused) return;
        _paused = false;
        try
        {
            if (_isGif)
                _gifRecorder?.Resume();
            else
                _recorder?.Resume();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to resume recording", ex);
        }
        _clickOverlay?.SetPaused(false);
        _keyOverlay?.SetPaused(false);
        Log.Info("Recording resumed");
    }

    private void StopRecording(bool discard)
    {
        if (!IsRecording || _stopping) return;
        _stopping = true;
        _discard = discard;
        CloseBar();
        CloseOverlays(); // also releases the mouse/keyboard hooks
        try
        {
            if (_isGif)
                StopGif();
            else if (_recorder is not null)
                _recorder.Stop(); // finalization continues in OnMp4Complete
            else
                CleanupAfterFailure(); // should not happen; avoid a stuck session
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop recording", ex);
            CleanupAfterFailure();
        }
    }

    private void StopGif()
    {
        var recorder = _gifRecorder;
        string tempPath = _tempPath!;
        bool discard = _discard;
        if (recorder is null)
        {
            CleanupAfterFailure();
            return;
        }
        // GifRecorder.Stop blocks while the encoder drains its queue.
        Task.Run(() =>
        {
            bool ok = recorder.Stop();
            _dispatcher.InvokeAsync(() =>
            {
                _gifRecorder = null;
                if (!ok || discard)
                    TryDelete(tempPath);
                else
                    FinalizeFile(tempPath, "gif");
                EndSession();
            });
        });
    }

    // ScreenRecorderLib raises these on a background thread.

    private void OnMp4Complete(object? sender, RecordingCompleteEventArgs e)
    {
        var recorder = sender as Recorder;
        _dispatcher.InvokeAsync(() =>
        {
            if (recorder is not null && !ReferenceEquals(recorder, _recorder)) return; // stale event from an old session
            DisposeRecorder();
            if (_discard)
                TryDelete(e.FilePath);
            else
                FinalizeFile(e.FilePath, "mp4");
            EndSession();
        });
    }

    private void OnMp4Failed(object? sender, RecordingFailedEventArgs e)
    {
        var recorder = sender as Recorder;
        _dispatcher.InvokeAsync(() =>
        {
            if (recorder is not null && !ReferenceEquals(recorder, _recorder)) return; // stale event from an old session
            Log.Error($"MP4 recording failed: {e.Error}");
            DisposeRecorder();
            TryDelete(_tempPath);
            CloseBar(); // failure can happen without the user pressing Stop
            CloseOverlays();
            EndSession();
        });
    }

    /// <summary>
    /// Moves the finished file into the save folder, adds it to history, and
    /// shows the completion toast (Open / Reveal / Edit… for MP4).
    /// </summary>
    private void FinalizeFile(string tempPath, string extension)
    {
        try
        {
            string folder = _settings.Current.SaveFolder;
            Directory.CreateDirectory(folder);
            string name = FileNamer.Next(_settings, extension);
            string finalPath = Path.Combine(folder, name);
            for (int n = 2; File.Exists(finalPath); n++)
                finalPath = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            File.Move(tempPath, finalPath);

            try { _history.AddFile(finalPath); }
            catch (Exception ex) { Log.Error("Failed to add recording to history", ex); }

            try
            {
                string savedPath = finalPath;
                bool isGif = extension.Equals("gif", StringComparison.OrdinalIgnoreCase);
                Action? onEdit = isGif
                    ? null
                    : () => new VideoEditorWindow(savedPath, _settings, _history).Show();
                new RecordingToastWindow(savedPath, onEdit).Show();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to show recording toast", ex);
            }

            Log.Info($"Recording saved to {finalPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to finalize recording (file left at {tempPath})", ex);
        }
    }

    private void EndSession()
    {
        IsRecording = false;
        _stopping = false;
        _discard = false;
        _paused = false;
        _tempPath = null;
    }

    private void CleanupAfterFailure()
    {
        DisposeRecorder();
        string? temp = _tempPath;
        if (_gifRecorder is not null)
        {
            var recorder = _gifRecorder;
            _gifRecorder = null;
            // Stop releases the file handle; only then can the temp file go.
            Task.Run(() =>
            {
                recorder.Stop();
                TryDelete(temp);
            });
        }
        else
        {
            TryDelete(temp);
        }
        CloseBar();
        CloseOverlays();
        EndSession();
    }

    private void DisposeRecorder()
    {
        if (_recorder is null) return;
        try
        {
            _recorder.OnRecordingComplete -= OnMp4Complete;
            _recorder.OnRecordingFailed -= OnMp4Failed;
            _recorder.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to dispose recorder", ex);
        }
        _recorder = null;
    }

    private void CloseBar()
    {
        if (_bar is null) return;
        try { _bar.Close(); }
        catch (Exception ex) { Log.Error("Failed to close recording bar", ex); }
        _bar = null;
    }

    private void CloseOverlays()
    {
        if (_clickOverlay is not null)
        {
            try { _clickOverlay.Close(); }
            catch (Exception ex) { Log.Error("Failed to close click-highlight overlay", ex); }
            _clickOverlay = null;
        }
        if (_keyOverlay is not null)
        {
            try { _keyOverlay.Close(); }
            catch (Exception ex) { Log.Error("Failed to close keystroke overlay", ex); }
            _keyOverlay = null;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to delete temp recording {path}", ex);
        }
    }
}
