using System.Diagnostics;
using System.Windows;
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
    private const int OverlayDismissDelayMs = 80;

    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly Dispatcher _dispatcher;

    private bool _flowActive;   // chooser/selector currently open
    private bool _stopping;     // stop requested, finalization in flight
    private bool _discard;      // cancel pressed: throw the file away
    private bool _restarting;   // stop requested as part of a restart
    private bool _paused;
    private bool _isGif;
    private bool _desktopIconsHidden; // we hid the desktop icons; restore them on teardown
    private Recorder? _recorder;
    private GifRecorder? _gifRecorder;
    private FastRecordingControlBar? _bar;
    private IRecordingOverlay? _clickOverlay;
    private IRecordingOverlay? _keyOverlay;
    private string? _tempPath;

    // Cached region + chosen options so Restart can re-run capture without
    // re-showing the options/region/countdown dialogs.
    private CaptureParameters? _lastCapture;

    private sealed record CaptureParameters(
        SD.Rectangle ScreenRect,
        bool IsGif,
        bool RecordMicrophone,
        bool RecordSystemAudio,
        bool CaptureCursor,
        bool ShowClickHighlights,
        bool ShowKeystrokes,
        string WebcamPosition,
        int WebcamSizePercent,
        string? WebcamDeviceName,
        string? MicrophoneDeviceName,
        int RecordingFps,
        int VideoQuality,
        int GifFps);

    public RecordingController(SettingsService settings, HistoryService history)
    {
        _settings = settings;
        _history = history;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsRecording { get; private set; }

    public void Shutdown()
    {
        _restarting = false; // never spin up a fresh recording while tearing down
        try
        {
            // The GIF recorder is stopped unconditionally below; only the MP4
            // recorder needs an explicit stop here.
            if (!_isGif)
                _recorder?.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop recording during shutdown", ex);
        }

        DisposeRecorder();
        if (_gifRecorder is not null)
        {
            try { _gifRecorder.Stop(); }
            catch (Exception ex) { Log.Error("Failed to stop GIF recording during shutdown", ex); }
            _gifRecorder = null;
        }

        TryDelete(_tempPath);
        CloseBar();
        CloseOverlays();
        EndSession();
    }

    /// <summary>Hotkey/tray entry point: starts the recording flow, or stops the active recording.</summary>
    public void ToggleFlow() => ToggleFlow(pickDisplay: false);

    public void ToggleDisplayFlow() => ToggleFlow(pickDisplay: true);

    private async void ToggleFlow(bool pickDisplay)
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
            await StartFlowAsync(pickDisplay);
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

    private async Task StartFlowAsync(bool pickDisplay)
    {
        var dialog = FastRecordingOptionsDialog.Create(_settings.Current);
        PerfLog.TrackFirstShown(dialog, "record options");
        bool isGif;
        bool recordMicrophone;
        bool recordSystemAudio;
        bool captureCursor;
        bool showClickHighlights;
        bool showKeystrokes;
        int countdownSeconds;
        string webcamPosition;
        int webcamSizePercent;
        string? webcamDeviceName;
        string? microphoneDeviceName;
        int recordingFps;
        int videoQuality;
        int gifFps;
        try
        {
            if (await dialog.ShowAsync() != WF.DialogResult.OK) return;

            isGif = dialog.IsGif;
            recordMicrophone = dialog.RecordMicrophone;
            recordSystemAudio = dialog.RecordSystemAudio;
            captureCursor = dialog.CaptureCursor;
            showClickHighlights = dialog.ShowClickHighlights;
            showKeystrokes = dialog.ShowKeystrokes;
            countdownSeconds = dialog.CountdownSeconds;
            webcamPosition = dialog.WebcamPosition;
            webcamSizePercent = dialog.WebcamSizePercent;
            webcamDeviceName = dialog.WebcamDeviceName;
            microphoneDeviceName = dialog.MicrophoneDeviceName;
            recordingFps = dialog.RecordingFps;
            videoQuality = dialog.VideoQuality;
            gifFps = dialog.GifFps;
        }
        finally
        {
            FastRecordingOptionsDialog.Return(dialog);
        }

        // Remember the choices as new defaults. Deliberately no Save() here —
        // the orchestrating app persists settings (Save re-registers hotkeys).
        var s = _settings.Current;
        s.RecordAudio = recordMicrophone;
        s.RecordSystemAudio = recordSystemAudio;
        s.CaptureCursor = captureCursor;
        s.ShowClickHighlights = showClickHighlights;
        s.ShowKeystrokes = showKeystrokes;
        s.RecordingCountdownSeconds = countdownSeconds;
        s.WebcamOverlayPosition = webcamPosition;
        s.WebcamOverlaySizePercent = webcamSizePercent;
        s.RecordingFps = recordingFps;
        s.GifFps = gifFps;

        RecordingRegionSelection selection;
        if (pickDisplay)
        {
            SD.Rectangle? display = FastDisplayPickerDialog.ChooseDisplay();
            if (display is null)
                return;
            selection = RecordingRegionSelection.FromDisplay(display.Value);
        }
        else
        {
            var selector = FastRegionSelectorDialog.Rent(
                () => Task.Run(() => WindowEnumerator.GetTopLevelWindows()),
                settings: null);
            PerfLog.TrackFirstShown(selector, "record selector");
            SD.Rectangle region;
            try
            {
                if (await selector.ShowAsync() != WF.DialogResult.OK || selector.SelectedRegionPx is not SD.Rectangle selected)
                    return;
                region = selected;
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }

            selection = RecordingRegionSelection.FromVirtualSelection(region, CaptureService.VirtualScreen);
        }

        await Task.Delay(OverlayDismissDelayMs);

        // H.264 needs even dimensions; region selections also need the virtual
        // desktop origin shifted into physical screen coordinates.
        SD.Rectangle screenRect = selection.ScreenRect;
        if (!selection.IsUsable)
        {
            Log.Error($"Recording region too small after rounding: {screenRect}");
            return;
        }

        if (countdownSeconds > 0)
        {
            using var countdown = new FastRecordingCountdownWindow(countdownSeconds, screenRect);
            PerfLog.TrackFirstShown(countdown, "record countdown");
            if (countdown.ShowDialog() != WF.DialogResult.OK) return;
        }

        var parameters = new CaptureParameters(
            screenRect,
            isGif,
            recordMicrophone,
            recordSystemAudio,
            captureCursor,
            showClickHighlights,
            showKeystrokes,
            webcamPosition,
            webcamSizePercent,
            webcamDeviceName,
            microphoneDeviceName,
            recordingFps,
            videoQuality,
            gifFps);

        StartCapture(parameters, trackBarPerf: true);
    }

    /// <summary>
    /// Brings up the overlays, recorder, and control bar for the given parameters.
    /// Shared by the initial flow and Restart, so it must not show any dialogs.
    /// </summary>
    private void StartCapture(CaptureParameters p, bool trackBarPerf)
    {
        _lastCapture = p;
        _isGif = p.IsGif;
        _discard = false;
        _stopping = false;
        _restarting = false;
        _paused = false;
        SD.Rectangle screenRect = p.ScreenRect;
        string tempDir = Path.Combine(Path.GetTempPath(), "WinShot");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording-{Guid.NewGuid():N}.{(_isGif ? "gif" : "mp4")}");

        // Honor the "hide desktop icons during capture" preference. Restored on
        // stop/cancel/restart/failure via RestoreDesktopIcons().
        if (_settings.Current.HideDesktopIconsDuringCapture && DesktopIcons.Visible)
        {
            try
            {
                DesktopIcons.Hide();
                _desktopIconsHidden = true;
            }
            catch (Exception ex)
            {
                Log.Error("Failed to hide desktop icons for recording", ex);
                _desktopIconsHidden = false;
            }
        }

        // Overlays go up before capture starts so the first frames already
        // have them. Both are click-through and intentionally visible to the
        // capture (rings/pills belong in the output).
        var overlays = RecordingOverlayStartup.Start(
            p.ShowClickHighlights,
            p.ShowKeystrokes,
            () => new FastClickHighlightOverlayWindow(screenRect),
            () => new FastKeystrokeOverlayWindow(screenRect),
            Log.Error);
        _clickOverlay = overlays.ClickOverlay;
        _keyOverlay = overlays.KeyOverlay;

        try
        {
            if (_isGif)
                StartGif(screenRect, p.CaptureCursor, p.GifFps);
            else
                StartMp4(screenRect, p.RecordMicrophone, p.RecordSystemAudio, p.CaptureCursor, p.WebcamPosition, p.WebcamSizePercent, p.WebcamDeviceName, p.MicrophoneDeviceName, p.RecordingFps, p.VideoQuality);
        }
        catch
        {
            // Make sure icons come back even if the recorder failed to start.
            RestoreDesktopIcons();
            CloseOverlays();
            throw;
        }

        _bar = new FastRecordingControlBar();
        _bar.StopRequested += () => StopRecording(discard: false);
        _bar.CancelRequested += () => StopRecording(discard: true);
        _bar.PauseRequested += PauseRecording;
        _bar.ResumeRequested += ResumeRecording;
        _bar.RestartRequested += RestartRecording;
        if (trackBarPerf)
            PerfLog.TrackFirstShown(_bar, "record control bar");
        _bar.Show();

        IsRecording = true;
        Log.Info($"Recording started ({(_isGif ? "GIF" : "MP4")}) {screenRect}");
    }

    private static void LogPerf(string metricName, Stopwatch sw) =>
        Log.Info($"Perf {metricName}: {sw.ElapsedMilliseconds} ms");

    private static void TrackFirstRender(Window window, string metricName)
    {
        var sw = Stopwatch.StartNew();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (handler is not null)
                window.ContentRendered -= handler;
            LogPerf($"{metricName} first render", sw);
        };
        window.ContentRendered += handler;
    }

    private void StartGif(SD.Rectangle screenRect, bool captureCursor, int gifFps)
    {
        _gifRecorder = new GifRecorder(screenRect, gifFps, _tempPath!, captureCursor);
        _gifRecorder.Start();
    }

    private void StartMp4(
        SD.Rectangle screenRect,
        bool micAudio,
        bool systemAudio,
        bool captureCursor,
        string webcamPosition,
        int webcamSizePercent,
        string? webcamDeviceName,
        string? microphoneDeviceName,
        int recordingFps,
        int videoQuality)
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

        var audio = RecordingAudioSelection.FromChoices(micAudio, systemAudio);
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
                Quality = Math.Clamp(videoQuality, 1, 100),
                Framerate = Math.Clamp(recordingFps, 1, 120),
                IsHardwareEncodingEnabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audio.IsAudioEnabled,
                IsInputDeviceEnabled = audio.IsInputDeviceEnabled,
                IsOutputDeviceEnabled = audio.IsOutputDeviceEnabled,
                // null/empty => recorder uses the system default input device.
                AudioInputDevice = micAudio && !string.IsNullOrEmpty(microphoneDeviceName)
                    ? microphoneDeviceName
                    : null,
            },
            // Click feedback comes from our own overlay window (shared with the
            // GIF path); ScreenRecorderLib's built-in click detection stays off.
            MouseOptions = new MouseOptions { IsMousePointerEnabled = captureCursor },
        };

        ApplyWebcamOverlay(options, webcamPosition, webcamSizePercent, webcamDeviceName, rect);

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnMp4Complete;
        _recorder.OnRecordingFailed += OnMp4Failed;
        _recorder.Record(_tempPath!);
    }

    /// <summary>
    /// Adds a webcam picture-in-picture overlay (~22 % of the region width)
    /// anchored at the chosen corner. Skipped quietly when no camera exists.
    /// </summary>
    private static void ApplyWebcamOverlay(
        RecorderOptions options,
        string webcamPosition,
        int webcamSizePercent,
        string? webcamDeviceName,
        SD.Rectangle rect)
    {
        if (!RecordingWebcamOverlayLayout.TryCreate(rect, webcamPosition, webcamSizePercent, out var layout))
            return;

        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            if (cameras is null || cameras.Count == 0)
            {
                Log.Info("Webcam overlay requested but no video capture device was found");
                return;
            }

            // Honor the chosen camera when it's still present; otherwise fall back
            // to the first available device (matches the dialog's default).
            string deviceName = cameras[0].DeviceName;
            if (!string.IsNullOrEmpty(webcamDeviceName) &&
                cameras.Any(c => string.Equals(c.DeviceName, webcamDeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                deviceName = webcamDeviceName;
            }

            var overlay = new VideoCaptureOverlay(deviceName)
            {
                AnchorPoint = layout.Position switch
                {
                    "fullscreen" or "top-left" => Anchor.TopLeft,
                    "top-right" => Anchor.TopRight,
                    "bottom-left" => Anchor.BottomLeft,
                    _ => Anchor.BottomRight,
                },
                Offset = new ScreenSize(layout.OffsetPx, layout.OffsetPx),
                Size = new ScreenSize(layout.Width, layout.Height),
                Stretch = layout.IsFullscreen ? StretchMode.UniformToFill : StretchMode.Uniform,
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
        var result = RecordingPauseCoordinator.Pause(
            IsRecording,
            _stopping,
            _paused,
            PauseActiveRecorder,
            SetOverlaysPaused,
            Log.Error);

        _paused = result.IsPaused;
        if (result.Changed)
            Log.Info("Recording paused");
    }

    private void ResumeRecording()
    {
        var result = RecordingPauseCoordinator.Resume(
            IsRecording,
            _stopping,
            _paused,
            ResumeActiveRecorder,
            SetOverlaysPaused,
            Log.Error);

        _paused = result.IsPaused;
        if (result.Changed)
            Log.Info("Recording resumed");
    }

    private void PauseActiveRecorder()
    {
        if (_isGif)
            _gifRecorder?.Pause();
        else
            _recorder?.Pause();
    }

    private void ResumeActiveRecorder()
    {
        if (_isGif)
            _gifRecorder?.Resume();
        else
            _recorder?.Resume();
    }

    private void SetOverlaysPaused(bool paused)
    {
        _clickOverlay?.SetPaused(paused);
        _keyOverlay?.SetPaused(paused);
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
            _dispatcher.InvokeAsync(async () =>
            {
                _gifRecorder = null;
                if (!ok || discard)
                    await Task.Run(() => TryDelete(tempPath));
                else
                    await FinalizeFileAsync(tempPath, "gif");
                EndSession();
            });
        });
    }

    // ScreenRecorderLib raises these on a background thread.

    private void OnMp4Complete(object? sender, RecordingCompleteEventArgs e)
    {
        var recorder = sender as Recorder;
        _dispatcher.InvokeAsync(async () =>
        {
            if (recorder is not null && !ReferenceEquals(recorder, _recorder)) return; // stale event from an old session
            DisposeRecorder();
            if (_discard)
                await Task.Run(() => TryDelete(e.FilePath));
            else
                await FinalizeFileAsync(e.FilePath, "mp4");
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
    private async Task FinalizeFileAsync(string tempPath, string extension)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string folder = _settings.Current.SaveFolder;
            string name = FileNamer.Next(_settings, extension);
            string finalPath = await Task.Run(() =>
            {
                string movedPath = RecordingFileFinalizer.MoveToUniqueFinalPath(tempPath, folder, name);
                try { _history.AddFile(movedPath); }
                catch (Exception ex) { Log.Error("Failed to add recording to history", ex); }
                return movedPath;
            });

            try
            {
                string savedPath = finalPath;
                bool isGif = extension.Equals("gif", StringComparison.OrdinalIgnoreCase);
                Action? onEdit = isGif
                    ? null
                    : () =>
                    {
                        var editor = new VideoEditorWindow(savedPath, _settings, _history);
                        TrackFirstRender(editor, "video editor window");
                        editor.Show();
                    };
                var toast = new FastRecordingToastWindow(savedPath, onEdit);
                PerfLog.TrackFirstShown(toast, "recording toast");
                toast.Show();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to show recording toast", ex);
            }

            Log.Info($"Recording saved to {finalPath}");
            LogPerf("recording finalize", sw);
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
        RestoreDesktopIcons();

        if (_restarting && _lastCapture is { } capture)
        {
            _restarting = false;
            // Re-enter on a fresh dispatcher turn so the stop chain fully unwinds
            // (recorder disposed, file deleted) before the new recorder starts.
            _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    StartCapture(capture, trackBarPerf: false);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to restart recording", ex);
                    CleanupAfterFailure();
                }
            });
        }
    }

    /// <summary>
    /// Discards the in-progress recording and starts a fresh one over the same
    /// region with the same options — no options/region/countdown dialogs.
    /// </summary>
    private void RestartRecording()
    {
        if (!IsRecording || _stopping || _lastCapture is null) return;
        _restarting = true;
        Log.Info("Recording restart requested");
        StopRecording(discard: true);
    }

    private void RestoreDesktopIcons()
    {
        if (!_desktopIconsHidden) return;
        _desktopIconsHidden = false;
        try { DesktopIcons.Show(); }
        catch (Exception ex) { Log.Error("Failed to restore desktop icons after recording", ex); }
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
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to delete temp recording {path}", ex);
                return;
            }
        }

        Log.Error($"Failed to delete temp recording {path}", lastError);
    }
}
