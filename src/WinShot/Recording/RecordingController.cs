using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using System.Collections.Specialized;
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
    private bool _notificationsSilenced; // we turned notification banners off; restore on teardown
    private Recorder? _recorder;
    private GifRecorder? _gifRecorder;
    private FastRecordingControlBar? _bar;
    private IRecordingOverlay? _clickOverlay;
    private IRecordingOverlay? _keyOverlay;
    private FastRecordingDimOverlay? _dimOverlay;
    private readonly Stopwatch _sessionElapsed = new();
    private DispatcherTimer? _trayTimer;
    private bool _elapsedEnabled;
    private string? _tempPath;
    // The sources the active MP4 recorder is composing, so cursor capture can be toggled on
    // each of them while a take runs. Held as the objects rather than their IDs because the
    // live-update API takes a source carrying the new values, matched back by its ID.
    private DisplayRecordingSource[] _recordingSources = Array.Empty<DisplayRecordingSource>();

    // Cached region + chosen options so Restart can re-run capture without
    // re-showing the options/region/countdown dialogs.
    private CaptureParameters? _lastCapture;

    private sealed record CaptureParameters(
        SD.Rectangle ScreenRect,
        SD.Rectangle[]? SourceDisplays,
        bool IsGif,
        bool RecordMicrophone,
        bool RecordSystemAudio,
        bool CaptureCursor,
        bool ShowClickHighlights,
        bool ShowKeystrokes,
        KeystrokeOverlayOptions KeystrokeOptions,
        bool HideCursorWhenTyping,
        string WebcamPosition,
        int WebcamSizePercent,
        string? WebcamDeviceName,
        string? MicrophoneDeviceName,
        int RecordingFps,
        int VideoQuality,
        int GifFps,
        bool ShowControls,
        bool ShowTimer,
        bool DimScreen,
        bool ScaleHiDpiVideo,
        string MaxResolution,
        bool RecordAudioMono,
        int GifQuality,
        string GifSize,
        bool OptimizeGifs);

    public RecordingController(SettingsService settings, HistoryService history)
    {
        _settings = settings;
        _history = history;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsRecording { get; private set; }

    /// <summary>The current strict WinShot temp path, exposed only so startup
    /// recovery can always exclude it from orphan discovery.</summary>
    internal string? ActiveTempPath => _tempPath;

    /// <summary>Recovery UI must not compete with the chooser, recorder, stop,
    /// or finalization path.</summary>
    internal bool BlocksStartupRecovery =>
        _flowActive || IsRecording || _stopping || _tempPath is not null;

    /// <summary>True while the recording chooser/selector UI is open (NOT while merely
    /// recording — screenshots during a recording are fine). Capture flows check this so
    /// two full-screen selectors never stack and z-order-fight each other.</summary>
    public bool SelectionUiActive => _flowActive;

    public event Action<TimeSpan?>? RecordingElapsedChanged;

    /// <summary>Raised whenever a take starts or ends. Separate from the elapsed event, which
    /// only fires while "Display recording time" is on — the tray icon has to say a recording
    /// is live regardless of whether the user asked for a clock.</summary>
    public event Action<bool>? RecordingActiveChanged;

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

        // A keep-stop may be mid-finalization (its dispatcher continuation dies with the
        // app): the temp file is that recording's ONLY copy, and startup recovery restores
        // it next launch. Only delete when the user never asked to keep it.
        if (_stopping && !_discard)
        {
            Log.Info($"Exiting during recording finalization; keeping {_tempPath} for startup recovery");
            RestoreNotifications(); // the app is going away; never leave banners switched off
            CloseBar();
            CloseOverlays();
            CloseDimOverlay();
            StopElapsedTimer();
            return;
        }

        TryDelete(_tempPath);
        RestoreNotifications();
        CloseBar();
        CloseOverlays();
        CloseDimOverlay();
        StopElapsedTimer();
        EndSession();
    }

    /// <summary>Hotkey/tray entry point: starts the recording flow, or stops the active recording.</summary>
    /// <summary>Stops and saves the running take. For the tray's stop-square icon, which is an
    /// affordance and has to do what it shows.</summary>
    public void RequestStop()
    {
        if (IsRecording)
            StopRecording(discard: false);
    }

    public void ToggleFlow() => ToggleFlow(pickDisplay: false, selectedVirtualRegion: null);

    public void ToggleFlowWithRegion(SD.Rectangle selectedVirtualRegion) =>
        ToggleFlow(pickDisplay: false, selectedVirtualRegion);

    public void ToggleDisplayFlow() => ToggleFlow(pickDisplay: true, selectedVirtualRegion: null);

    private async void ToggleFlow(bool pickDisplay, SD.Rectangle? selectedVirtualRegion)
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
            await StartFlowAsync(pickDisplay, selectedVirtualRegion);
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

    /// <summary>
    /// Region first, options second. The selector opens straight away — with the remembered or
    /// pre-picked region already pending where there is one — and the two-card HUD anchored to
    /// that selection carries the toggles and the two record actions. Everything the HUD does
    /// not show (frame rates, quality, devices, countdown) is a persisted setting and stays in
    /// Settings, which is where the old fifteen-row modal was reading it from anyway.
    ///
    /// Picking whole displays keeps the modal: there is no selection to anchor a HUD to.
    /// </summary>
    private async Task StartFlowAsync(bool pickDisplay, SD.Rectangle? selectedVirtualRegion)
    {
        Settings s = _settings.Current;

        if (pickDisplay)
        {
            await StartDisplayFlowAsync(s);
            return;
        }

        // A remembered or All-in-One region opens the HUD already framed rather than skipping
        // straight past the options.
        SD.Rectangle? initialSelection = null;
        if (RecordingAreaMemory.TryResolve(
                selectedVirtualRegion,
                s.RememberLastSelection,
                s.LastRecordingRegion,
                CaptureService.VirtualScreen,
                out RecordingRegionSelection remembered) &&
            remembered.IsUsable)
        {
            initialSelection = remembered.ScreenRect;
            Log.Info(selectedVirtualRegion is not null
                ? "Opening the recording HUD on the area selected in All-in-One"
                : "Opening the recording HUD on the remembered recording area");
        }

        RecordingHudChoice choice;
        SD.Rectangle region;
        var selector = FastRegionSelectorDialog.Rent(
            () => Task.Run(() => WindowEnumerator.GetTopLevelWindows()),
            settings: null);
        PerfLog.TrackFirstShown(selector, "record selector");
        try
        {
            // Same feel as screenshot area capture: hover snaps to windows/elements, click
            // takes the snap, click-drag draws a marquee — but a click frames the take rather
            // than starting it, because the HUD still has to be answered.
            WF.DialogResult result = await selector.ShowAsync(
                elementHover: true,
                hint: "Drag to record part of the screen, or click a window to record it.",
                recordingHud: RecordingHudToggles.FromSettings(s),
                initialSelection: initialSelection);
            if (result != WF.DialogResult.OK ||
                selector.SelectedRegionPx is not SD.Rectangle selected ||
                selector.RecordingChoice is not RecordingHudChoice picked)
            {
                return;
            }
            region = selected;
            choice = picked;
        }
        finally
        {
            FastRegionSelectorDialog.Return(selector);
        }

        // Remember the HUD's answers as the new defaults. Deliberately no Save() here — the
        // orchestrating app persists settings (Save re-registers hotkeys).
        s.RecordAudio = choice.Toggles.Microphone;
        s.RecordSystemAudio = choice.Toggles.SystemAudio;
        s.ShowClickHighlights = choice.Toggles.Clicks;
        s.ShowKeystrokes = choice.Toggles.Keystrokes;
        s.SetWebcamOverlayPosition(
            WebcamPositionFor(choice.Toggles.Webcam, s.WebcamOverlayPosition, s.WebcamOverlayCorner));

        RecordingRegionSelection selection =
            RecordingRegionSelection.FromVirtualSelection(region, CaptureService.VirtualScreen);
        await BeginTakeAsync(s, selection, sourceDisplays: null, choice.IsGif, choice.Toggles);
    }

    /// <summary>
    /// Whole-display recording: the region-first HUD has nothing to anchor to, so this keeps
    /// the options modal as the fallback the sweep called for.
    /// </summary>
    private async Task StartDisplayFlowAsync(Settings s)
    {
        var dialog = FastRecordingOptionsDialog.Create(s);
        PerfLog.TrackFirstShown(dialog, "record options");
        bool isGif;
        RecordingHudToggles toggles;
        try
        {
            if (await dialog.ShowAsync() != WF.DialogResult.OK)
                return;

            isGif = dialog.IsGif;
            toggles = new RecordingHudToggles(
                dialog.RecordMicrophone,
                dialog.RecordSystemAudio,
                !string.Equals(dialog.WebcamPosition, RecordingOptions.WebcamOff, StringComparison.Ordinal),
                dialog.ShowClickHighlights,
                dialog.ShowKeystrokes);

            s.RecordAudio = dialog.RecordMicrophone;
            s.RecordSystemAudio = dialog.RecordSystemAudio;
            s.CaptureCursor = dialog.CaptureCursor;
            s.ShowClickHighlights = dialog.ShowClickHighlights;
            s.ShowKeystrokes = dialog.ShowKeystrokes;
            s.RecordingCountdownSeconds = dialog.CountdownSeconds;
            s.SetWebcamOverlayPosition(dialog.WebcamPosition);
            s.WebcamOverlaySizePercent = dialog.WebcamSizePercent;
            s.RecordingFps = dialog.RecordingFps;
            s.GifFps = dialog.GifFps;
            // The modal is the only surface with live device pickers, so its picks become the
            // persisted ones the region-first flow reads.
            s.RecordingMicrophoneDevice = dialog.MicrophoneDeviceName ?? "";
            s.RecordingWebcamDevice = dialog.WebcamDeviceName ?? "";
            s.RecordingVideoQuality = RecordingOptions.ClampVideoQuality(dialog.VideoQuality);
        }
        finally
        {
            FastRecordingOptionsDialog.Return(dialog);
        }

        SD.Rectangle[]? displays = FastDisplayPickerDialog.ChooseDisplays();
        if (displays is null || displays.Length == 0)
            return;

        await BeginTakeAsync(
            s,
            RecordingRegionSelection.FromDisplays(displays),
            displays,
            isGif,
            toggles);
    }

    /// <summary>Everything both entry points share: the countdown, the region bookkeeping, and
    /// building the capture parameters out of settings plus the chosen toggles.</summary>
    private async Task BeginTakeAsync(
        Settings s,
        RecordingRegionSelection selection,
        SD.Rectangle[]? sourceDisplays,
        bool isGif,
        RecordingHudToggles toggles)
    {
        await Task.Delay(OverlayDismissDelayMs);

        // H.264 needs even dimensions; region selections also need the virtual
        // desktop origin shifted into physical screen coordinates.
        SD.Rectangle screenRect = selection.ScreenRect;
        if (!selection.IsUsable)
        {
            Log.Error($"Recording region too small after rounding: {screenRect}");
            return;
        }

        if (s.RememberLastSelection)
            s.LastRecordingRegion = PreviousRegion.Format(screenRect);

        int countdownSeconds = RecordingOptions.ClampCountdownSeconds(s.RecordingCountdownSeconds);
        if (countdownSeconds > 0)
        {
            using var countdown = new FastRecordingCountdownWindow(countdownSeconds, screenRect);
            PerfLog.TrackFirstShown(countdown, "record countdown");
            if (countdown.ShowDialog() != WF.DialogResult.OK) return;
        }

        RecordingSessionUiOptions sessionUi = RecordingSessionUiOptions.FromSettings(s);
        var parameters = new CaptureParameters(
            screenRect,
            sourceDisplays,
            isGif,
            toggles.Microphone,
            toggles.SystemAudio,
            s.CaptureCursor,
            toggles.Clicks,
            toggles.Keystrokes,
            KeystrokeOverlayOptions.FromSettings(s) with { Render = toggles.Keystrokes },
            s.HideCursorWhenTyping,
            WebcamPositionFor(toggles.Webcam, s.WebcamOverlayPosition, s.WebcamOverlayCorner),
            s.WebcamOverlaySizePercent,
            WebcamDeviceName: NullIfBlank(s.RecordingWebcamDevice),
            MicrophoneDeviceName: NullIfBlank(s.RecordingMicrophoneDevice),
            s.RecordingFps,
            RecordingOptions.ClampVideoQuality(s.RecordingVideoQuality),
            s.GifFps,
            sessionUi.ShowControls,
            sessionUi.ShowTimer,
            sessionUi.DimScreen,
            s.ScaleHiDpiVideo,
            s.RecordingMaxResolution,
            s.RecordAudioMono,
            s.GifQuality,
            s.GifSize,
            s.OptimizeGifs);

        StartCapture(parameters, trackBarPerf: true);
    }

    /// <summary>A blank device name means "whatever the system picks", which is what the
    /// recorder wants as null.</summary>
    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The HUD's webcam cell is a plain on/off, so turning it on restores whatever corner was
    /// last chosen and falls back to the default corner the first time. The corner comes from
    /// <see cref="Settings.WebcamOverlayCorner"/>, not from the position — the position is
    /// "off" exactly when this needs the memory, which is what used to lose it.
    /// </summary>
    internal static string WebcamPositionFor(
        bool enabled, string? storedPosition, string? rememberedCorner)
    {
        if (!enabled)
            return RecordingOptions.WebcamOff;

        string stored = RecordingOptions.NormalizeWebcamPosition(storedPosition);
        return string.Equals(stored, RecordingOptions.WebcamOff, StringComparison.Ordinal)
            ? RecordingOptions.RememberedWebcamCorner(null, rememberedCorner)
            : stored;
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

        // Same save/restore shape: a toast banner landing mid-take is exactly what this
        // setting exists to prevent, and the absence of the banner IS the whole feature.
        if (_settings.Current.DoNotDisturbWhileRecording)
        {
            FocusAssist.Suppress();
            _notificationsSilenced = true;
        }

        // Overlays go up before capture starts so the first frames already
        // have them. Both are click-through and intentionally visible to the
        // capture (rings/pills belong in the output).
        // The keystroke overlay is also the only thing in the app that knows when typing is
        // happening, so "hide the cursor while typing" starts it in render-off mode even when
        // the pill itself is turned off.
        // CaptureCursor is part of the condition because it is part of the subscription below:
        // without it nothing consumes TypingChanged, and the hook would be a WH_KEYBOARD_LL on
        // every keystroke of the take for no effect at all.
        bool needsKeyHook = p.ShowKeystrokes || (p.HideCursorWhenTyping && p.CaptureCursor);
        var overlays = RecordingOverlayStartup.Start(
            p.ShowClickHighlights,
            needsKeyHook,
            () => new FastClickHighlightOverlayWindow(screenRect),
            () =>
            {
                var overlay = new FastKeystrokeOverlayWindow(screenRect, p.KeystrokeOptions);
                if (p.HideCursorWhenTyping && p.CaptureCursor)
                    overlay.TypingChanged += OnTypingChanged;
                return overlay;
            },
            Log.Error);
        _clickOverlay = overlays.ClickOverlay;
        _keyOverlay = overlays.KeyOverlay;

        try
        {
            if (_isGif)
                StartGif(screenRect, p.CaptureCursor, p.GifFps, p.GifSize, p.GifQuality, p.OptimizeGifs);
            else
                StartMp4(p);
        }
        catch
        {
            // Make sure icons and notifications come back even if the recorder failed to start.
            RestoreDesktopIcons();
            RestoreNotifications();
            CloseOverlays();
            throw;
        }

        // Always one or the other: with dimming off the region used to have no on-screen
        // marker at all, so nothing said what was being recorded.
        _dimOverlay = new FastRecordingDimOverlay(screenRect, p.DimScreen);
        _dimOverlay.Show();

        if (p.ShowControls)
        {
            _bar = new FastRecordingControlBar(screenRect, p.ShowTimer, _settings.Current.HotkeyRecord);
            _bar.StopRequested += () => StopRecording(discard: false);
            _bar.CancelRequested += () => StopRecording(discard: true);
            _bar.PauseRequested += PauseRecording;
            _bar.ResumeRequested += ResumeRecording;
            _bar.RestartRequested += RestartRecording;
            if (trackBarPerf)
                PerfLog.TrackFirstShown(_bar, "record control bar");
            _bar.Show();
        }

        StartElapsedTimer(p.ShowTimer);

        IsRecording = true;
        RecordingActiveChanged?.Invoke(true);
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

    private void StartGif(
        SD.Rectangle screenRect,
        bool captureCursor,
        int gifFps,
        string gifSize,
        int gifQuality,
        bool optimizeGifs)
    {
        SD.Size outputSize = RecordingOutputSize.CalculateGif(screenRect.Size, gifSize);
        int paletteColors = RecordingOutputSize.GifPaletteColors(gifQuality, optimizeGifs);
        _gifRecorder = new GifRecorder(screenRect, gifFps, _tempPath!, captureCursor, outputSize, paletteColors);
        _gifRecorder.Start();
    }

    private void StartMp4(CaptureParameters p)
    {
        SD.Rectangle screenRect = p.ScreenRect;
        // Every display the selection touches (or the picked subset) is composed onto
        // one canvas mirroring the real monitor layout, then cropped to the selection —
        // so single-display, cross-display, and multi-display recordings share one path.
        var composition = RecordingDisplayComposition.Create(
            screenRect,
            WF.Screen.AllScreens.Select(candidate => (candidate.DeviceName, candidate.Bounds)).ToArray(),
            p.SourceDisplays);
        if (!composition.IsUsable)
            throw new InvalidOperationException("The selected region does not overlap a display.");
        SD.Rectangle crop = composition.SourceRect;

        var audio = RecordingAudioSelection.FromChoices(p.RecordMicrophone, p.RecordSystemAudio);
        SD.Size outputSize = RecordingOutputSize.CalculateVideo(
            crop.Size,
            p.MaxResolution,
            p.ScaleHiDpiVideo,
            RecordingMonitorDpi.ScaleFor(screenRect));
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = composition.Sources
                    .Select(source => (RecordingSourceBase)new DisplayRecordingSource(source.DeviceName)
                    {
                        Position = new ScreenPoint(source.CanvasPosition.X, source.CanvasPosition.Y),
                    })
                    .ToList(),
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                SourceRect = new ScreenRect(crop.X, crop.Y, crop.Width, crop.Height),
                OutputFrameSize = new ScreenSize(outputSize.Width, outputSize.Height),
                Stretch = StretchMode.Uniform,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality },
                Quality = Math.Clamp(p.VideoQuality, 1, 100),
                Framerate = Math.Clamp(p.RecordingFps, 1, 120),
                IsHardwareEncodingEnabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audio.IsAudioEnabled,
                IsInputDeviceEnabled = audio.IsInputDeviceEnabled,
                IsOutputDeviceEnabled = audio.IsOutputDeviceEnabled,
                ForceInputDeviceMono = RecordingAudioInputOptions.ShouldForceMono(p.RecordMicrophone, p.RecordAudioMono),
                // null/empty => recorder uses the system default input device.
                AudioInputDevice = p.RecordMicrophone && !string.IsNullOrEmpty(p.MicrophoneDeviceName)
                    ? p.MicrophoneDeviceName
                    : null,
            },
            // Click feedback comes from our own overlay window (shared with the
            // GIF path); ScreenRecorderLib's built-in click detection stays off.
            MouseOptions = new MouseOptions { IsMousePointerEnabled = p.CaptureCursor },
        };

        ApplyWebcamOverlay(options, p.WebcamPosition, p.WebcamSizePercent, p.WebcamDeviceName, crop);

        _recordingSources = options.SourceOptions.RecordingSources
            .OfType<DisplayRecordingSource>()
            .Where(source => !string.IsNullOrEmpty(source.ID))
            .ToArray();
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnMp4Complete;
        _recorder.OnRecordingFailed += OnMp4Failed;
        // Windows allows one active desktop duplication per output per process, and the
        // recorder duplicates every source display. The selector's warm capture cache
        // may still hold duplications from the region pick seconds ago — release them
        // or the recorder dies with "The parameter is incorrect" (E_INVALIDARG).
        DesktopDuplicationCapture.ReleaseResources(timeoutMs: 2000);
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
        if (_bar is { IsDisposed: false })
            _bar.SyncPaused(_paused); // a failed pause must not leave the bar showing "paused"
        if (result.Changed)
        {
            if (_elapsedEnabled)
                _sessionElapsed.Stop();
            Log.Info("Recording paused");
        }
        else if (IsRecording && !_stopping)
        {
            // SyncPaused snaps the glyph and the clock back; on its own that is
            // indistinguishable from a misclick, so the refusal has to say so.
            App.Toast(
                "Could not pause",
                "The recorder kept running. The take is still being captured.",
                severity: ToastSeverity.Error);
        }
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
        if (_bar is { IsDisposed: false })
            _bar.SyncPaused(_paused); // a failed resume must not leave the bar showing "recording"
        if (result.Changed)
        {
            if (_elapsedEnabled)
                _sessionElapsed.Start();
            Log.Info("Recording resumed");
        }
        else if (IsRecording && !_stopping)
        {
            App.Toast(
                "Could not resume",
                "The take is still paused. Stop it to keep what has been recorded.",
                severity: ToastSeverity.Error);
        }
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

    /// <summary>
    /// Takes the cursor out of the recording while the user is typing and puts it back when
    /// they stop — nobody wants to watch a pointer sit in the middle of a sentence. Applied
    /// live through the recorder's dynamic options (MP4) or the GIF encoder's own flag.
    /// </summary>
    private void OnTypingChanged(bool typing)
    {
        bool showCursor = !typing;
        try
        {
            if (_isGif)
            {
                if (_gifRecorder is { } gif)
                    gif.CaptureCursor = showCursor;
                return;
            }

            if (_recorder is not { } recorder || _recordingSources.Length == 0)
                return;

            var builder = recorder.GetDynamicOptionsBuilder();
            foreach (DisplayRecordingSource source in _recordingSources)
            {
                source.IsCursorCaptureEnabled = showCursor;
                builder.SetUpdatedRecordingSource(source);
            }
            builder.Apply();
        }
        catch (Exception ex)
        {
            // Best effort: a recorder that refuses the change simply keeps showing the cursor.
            Log.Error("Failed to change cursor capture while typing", ex);
        }
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
        // The click/keystroke overlays are deliberately visible to the capture, so they have
        // to go the instant the recorder stops. Everything else leaves on a fade: the bar
        // stays put in a "Saving…" state until the completion card replaces it (or, when the
        // take is being thrown away, fades out at once because there is nothing to wait for).
        CloseOverlays(); // also releases the mouse/keyboard hooks
        FadeOutDimOverlay();
        if (discard || _restarting)
            FadeOutBar();
        // Throwing the take away is the one irreversible branch and it was the only silent
        // one: the bar and the marker faded and the screen looked exactly as it does after a
        // stop-and-save with the completion card turned off. A toast, not a confirm dialog —
        // the discard cell is deliberately a one-press affordance.
        if (discard && !_restarting)
        {
            App.Toast(
                "Recording discarded",
                "Nothing was saved.",
                severity: ToastSeverity.Info);
        }
        else if (_bar is { IsDisposed: false })
            _bar.BeginSaving();
        try
        {
            if (_isGif)
                StopGif();
            else if (_recorder is not null)
                _recorder.Stop(); // finalization continues in OnMp4Complete
            else
            {
                string? tempPath = _tempPath;
                CleanupAfterFailure(preserveTemp: true); // should not happen; avoid a stuck session
                ShowRecordingFailure(RecordingFailureMessage.ForRecorderFailure(
                    tempPath,
                    "The recording engine was no longer available when Stop was pressed."));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to stop recording", ex);
            string? tempPath = _tempPath;
            CleanupAfterFailure(preserveTemp: true);
            ShowRecordingFailure(RecordingFailureMessage.ForRecorderFailure(
                tempPath, Core.FriendlyError.For(ex)));
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
                if (discard)
                    await Task.Run(() => TryDelete(tempPath));
                else if (!ok)
                {
                    Log.Error($"GIF recording failed to finish (file kept at {tempPath})");
                    ShowRecordingFailure(RecordingFailureMessage.ForRecorderFailure(
                        tempPath,
                        "The GIF encoder could not finish the recording."));
                }
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
            string? tempPath = _tempPath;
            FadeOutBar(); // failure can happen without the user pressing Stop
            CloseOverlays();
            FadeOutDimOverlay();
            EndSession();
            ShowRecordingFailure(RecordingFailureMessage.ForRecorderFailure(tempPath, e.Error));
        });
    }

    /// <summary>
    /// Moves the finished file into the save folder, adds it to history, and
    /// shows the Quick Access completion card (Pin / Copy / Save / Edit for MP4).
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
                RecordingFinalizationResult finalized = RecordingFileFinalizer.FinalizeToUniqueFinalPath(
                    tempPath,
                    folder,
                    name);
                if (finalized.TempFileRetained)
                    Log.Info($"Recording saved to {finalized.FinalPath}; temp duplicate retained at {tempPath}");
                try { _history.AddFile(finalized.FinalPath); }
                catch (Exception ex) { Log.Error("Failed to add recording to history", ex); }
                return finalized.FinalPath;
            });

            string savedPath = finalPath;
            bool isGif = extension.Equals("gif", StringComparison.OrdinalIgnoreCase);
            RecordingAfterActions actions = RecordingAfterActions.FromSettings(_settings.Current, isGif);
            Action onEdit = isGif
                ? () => OpenGifEditor(savedPath)
                : () => OpenVideoEditor(savedPath);

            if (actions.CopyFile)
            {
                try
                {
                    var files = new StringCollection { savedPath };
                    WF.Clipboard.SetFileDropList(files);
                }
                catch (Exception ex) { Log.Error("Failed to copy recording file", ex); }
            }

            if (actions.ShowCompletionOverlay)
            {
                try { RecordingCompletionOverlay.Show(savedPath, _settings, onEdit); }
                catch (Exception ex) { Log.Error("Failed to show recording overlay", ex); }
            }

            if (actions.OpenEditor)
            {
                try { onEdit(); }
                catch (Exception ex) { Log.Error("Failed to open the editor after recording", ex); }
            }

            Log.Info($"Recording saved to {finalPath}");
            LogPerf("recording finalize", sw);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to finalize recording (file left at {tempPath})", ex);
            ShowRecordingFailure(RecordingFailureMessage.ForFinalization(
                _settings.Current.SaveFolder,
                tempPath,
                ex));
        }
    }

    private void OpenGifEditor(string filePath)
    {
        var editor = new GifEditorWindow(filePath, _settings, _history);
        TrackFirstRender(editor, "gif editor window");
        editor.Show();
    }

    private void OpenVideoEditor(string filePath)
    {
        var editor = new VideoEditorWindow(filePath, _settings, _history);
        TrackFirstRender(editor, "video editor window");
        editor.Show();
    }

    private void EndSession()
    {
        bool wasRecording = IsRecording;
        IsRecording = false;
        _stopping = false;
        _discard = false;
        _paused = false;
        _tempPath = null;
        _recordingSources = Array.Empty<DisplayRecordingSource>(); // never carry a dead session's sources forward
        CloseDimOverlay();
        StopElapsedTimer();
        RestoreDesktopIcons();
        RestoreNotifications();
        // Finalization is done and the completion card (if any) is already on screen, so the
        // "Saving…" pill has said everything it had to say.
        FadeOutBar();
        if (wasRecording)
            RecordingActiveChanged?.Invoke(false);

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

    private void RestoreNotifications()
    {
        if (!_notificationsSilenced) return;
        _notificationsSilenced = false;
        FocusAssist.Restore();
    }

    private void CleanupAfterFailure(bool preserveTemp = false)
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
                if (!preserveTemp)
                    TryDelete(temp);
            });
        }
        else if (!preserveTemp)
        {
            TryDelete(temp);
        }
        FadeOutBar();
        CloseOverlays();
        FadeOutDimOverlay();
        EndSession();
    }

    private void ShowRecordingFailure(string message)
    {
        // The bar is TopMost, so a "Saving…" spinner would sit on top of the dialog saying the
        // save failed. It leaves before the bad news arrives.
        FadeOutBar();
        try
        {
            ThemedMessageDialog.Show(null, "Recording failed", message);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to show recording error", ex);
        }
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

    /// <summary>Lets the bar play its exit and close itself. The reference is dropped now, so
    /// nothing can close it out from under the tween.</summary>
    private void FadeOutBar()
    {
        FastRecordingControlBar? bar = _bar;
        _bar = null;
        if (bar is null || bar.IsDisposed) return;
        try { bar.FadeOutAndClose(); }
        catch (Exception ex) { Log.Error("Failed to fade out the recording bar", ex); }
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

    private void CloseDimOverlay()
    {
        if (_dimOverlay is null) return;
        try { _dimOverlay.Dispose(); }
        catch (Exception ex) { Log.Error("Failed to close recording dim overlay", ex); }
        _dimOverlay = null;
    }

    /// <summary>Same, but on a fade. The reference is dropped now so a later teardown cannot
    /// dispose the overlay mid-tween.</summary>
    private void FadeOutDimOverlay()
    {
        FastRecordingDimOverlay? overlay = _dimOverlay;
        _dimOverlay = null;
        if (overlay is null) return;
        try { overlay.FadeOutAndDispose(); }
        catch (Exception ex) { Log.Error("Failed to fade out the recording dim overlay", ex); }
    }

    private void StartElapsedTimer(bool enabled)
    {
        StopElapsedTimer();
        if (!enabled)
            return;

        _elapsedEnabled = true;
        _sessionElapsed.Restart();
        _trayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _trayTimer.Tick += OnElapsedTimerTick;
        _trayTimer.Start();
        RecordingElapsedChanged?.Invoke(TimeSpan.Zero);
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e) =>
        RecordingElapsedChanged?.Invoke(_sessionElapsed.Elapsed);

    private void StopElapsedTimer()
    {
        if (_trayTimer is not null)
        {
            _trayTimer.Stop();
            _trayTimer.Tick -= OnElapsedTimerTick;
            _trayTimer = null;
        }
        _elapsedEnabled = false;
        _sessionElapsed.Reset();
        RecordingElapsedChanged?.Invoke(null);
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
