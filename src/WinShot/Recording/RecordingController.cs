using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ScreenRecorderLib;
using WinShot.Capture;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

/// <summary>
/// Drives the screen-recording flow: format chooser → region pick → MP4
/// (ScreenRecorderLib) or GIF (timer + streaming encoder) → save folder,
/// history, and an Explorer window with the file selected.
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
    private bool _isGif;
    private Recorder? _recorder;
    private GifRecorder? _gifRecorder;
    private RecordingControlBar? _bar;
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
        var dialog = new RecordingOptionsDialog(_settings.Current.RecordAudio);
        if (dialog.ShowDialog() != true) return;

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

        _isGif = dialog.IsGif;
        _discard = false;
        _stopping = false;
        string tempDir = Path.Combine(Path.GetTempPath(), "WinShot");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording-{Guid.NewGuid():N}.{(_isGif ? "gif" : "mp4")}");

        if (_isGif)
            StartGif(screenRect);
        else
            StartMp4(screenRect, dialog.RecordAudio);

        _bar = new RecordingControlBar();
        _bar.StopRequested += () => StopRecording(discard: false);
        _bar.CancelRequested += () => StopRecording(discard: true);
        _bar.Show();

        IsRecording = true;
        Log.Info($"Recording started ({(_isGif ? "GIF" : "MP4")}) {screenRect}");
    }

    private void StartGif(SD.Rectangle screenRect)
    {
        _gifRecorder = new GifRecorder(screenRect, _settings.Current.GifFps, _tempPath!);
        _gifRecorder.Start();
    }

    private void StartMp4(SD.Rectangle screenRect, bool recordAudio)
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
                IsAudioEnabled = recordAudio,
                IsInputDeviceEnabled = recordAudio,
                IsOutputDeviceEnabled = false,
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnMp4Complete;
        _recorder.OnRecordingFailed += OnMp4Failed;
        _recorder.Record(_tempPath!);
    }

    private void StopRecording(bool discard)
    {
        if (!IsRecording || _stopping) return;
        _stopping = true;
        _discard = discard;
        CloseBar();
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
            EndSession();
        });
    }

    /// <summary>Moves the finished file into the save folder, adds it to history, and reveals it in Explorer.</summary>
    private void FinalizeFile(string tempPath, string extension)
    {
        try
        {
            string folder = _settings.Current.SaveFolder;
            Directory.CreateDirectory(folder);
            string name = CaptureService.DefaultFileName(extension);
            string finalPath = Path.Combine(folder, name);
            for (int n = 2; File.Exists(finalPath); n++)
                finalPath = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            File.Move(tempPath, finalPath);

            try { _history.AddFile(finalPath); }
            catch (Exception ex) { Log.Error("Failed to add recording to history", ex); }

            try { Process.Start("explorer.exe", $"/select,\"{finalPath}\""); }
            catch (Exception ex) { Log.Error("Failed to open Explorer for recording", ex); }

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
