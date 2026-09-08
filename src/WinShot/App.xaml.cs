using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Editor;
using WinShot.Editor.Background;
using WinShot.History;
using WinShot.Ocr;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Recording;
using WinShot.Scrolling;
using WinShot.SettingsUi;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot;

public partial class App : Application
{
    private const int OverlayDismissDelayMs = 80;
    private const int AutoCopyFailureCooldownMs = 8_000;

    private Mutex? _mutex;
    private bool _ownsMutex;
    private WF.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private CommandServer? _commandServer;
    private readonly SettingsService _settings = new();
    private HistoryService _history = null!;
    private RecordingController? _recording;
    private RecordingRecoveryStartup? _recordingRecovery;
    private bool _captureInProgress;
    private string? _pendingCaptureCommand;
    private bool _pendingCaptureDrainActive;
    private long _autoCopySuppressedUntilTick;
    internal DirectEditCaptureTestHooks? DirectEditTestHooks { get; set; }
    internal SettingsService SettingsForTest => _settings;

    private WF.ToolStripMenuItem? _updateMenuItem;
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? incomingCommand = e.Args.Length > 0 ? CommandServer.ParseCommand(e.Args[0]) : null;

        _mutex = new Mutex(true, "WinShot-SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // The mutex says another instance may exist — but verify one is actually serving
            // the command pipe before bowing out. A crashed instance can leave the named mutex
            // behind; without this probe WinShot would refuse to launch (no tray icon, no way
            // to recover short of a reboot).
            bool handledByRunningInstance = incomingCommand is not null
                ? CommandServer.TrySendToRunningInstance(incomingCommand)
                : CommandServer.IsInstanceRunning();

            if (handledByRunningInstance)
            {
                if (incomingCommand is null)
                    ThemedMessageDialog.Show(null, "WinShot is already running", "Look for it in the system tray.");
                Shutdown();
                return;
            }

            // Stale mutex with no live instance — continue as the primary so WinShot stays
            // launchable. We never acquired ownership, so OnExit won't try to release it.
            Log.Info("Single-instance mutex present but no live instance responded; starting as primary.");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            ShowBalloon("WinShot error", FriendlyError.For(args.Exception), ToastSeverity.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        // Before any window exists, and after the hand-off has had its chance to exit.
        // ThemeResources.ApplyAnimationPreference can only zero a control template's
        // transitions while its BeginStoryboards are still unsealed, and WPF seals them the
        // first time a style is applied - so whichever window opened first used to decide
        // whether "Animation effects: off" reached the WPF stack at all. The recovery dialog
        // below was one such first window. A `winshot <command>` launch that only forwards to
        // the running instance opens no window, so it should not parse the dictionary at all;
        // the "already running" dialog on that path loads it itself.
        // Deliberately below the two handlers above: with animation off this inflates ~146
        // deferred values and walks every template, and a bad pack URI in a repackaged build
        // would otherwise kill startup with nothing installed to log it.
        Core.ThemeResources.EnsureLoaded();

        // WinForms controls (the Fast* selectors, scrolling chrome) route UI-thread exceptions
        // through their own NativeWindow callback, which would otherwise pop the default .NET
        // "Unhandled exception" dialog — e.g. on a transient graphics-device error (0x8007001F),
        // common over RDP. Catch + log + continue instead; a capture overlay must never crash
        // the whole app. Must be set before any WinForms window is created on this thread.
        try { WF.Application.SetUnhandledExceptionMode(WF.UnhandledExceptionMode.CatchException); }
        catch (Exception ex) { Log.Error("Could not set WinForms unhandled-exception mode", ex); }
        WF.Application.ThreadException += (_, args) =>
            Log.Error("Unhandled WinForms UI exception (continuing)", args.Exception);

        _settings.Load();
        _history = new HistoryService(_settings);
        _recordingRecovery = new RecordingRecoveryStartup(
            excludedPaths => RecordingTempRecovery.Discover(excludedPaths: excludedPaths),
            candidate => RecordingTempRecovery.Recover(
                candidate.Path,
                _settings.Current.SaveFolder,
                FileNamer.Next(_settings, candidate.Extension)),
            recoveredPath => _history.AddFile(recoveredPath));
        _settings.Changed += RegisterHotkeys;
        // Every pin in the app gets a pencil chip, including the ones App does not build
        // itself (the recording completion card raises its own), so the editor handoff is
        // registered once here rather than at each construction site.
        FastPinWindow.DefaultEditHandler = OpenEditorFromPin;
        // The recognizer is cached per language, so pushing the choice here is enough:
        // the next capture rebuilds the engine, and a settings save never pays for it.
        _settings.Changed += () => OcrService.PreferredLanguage = _settings.Current.OcrLanguage;

        _ = Task.Run(() =>
        {
            ProtocolRegistrar.EnsureRegistered();
            ProtocolRegistrar.EnsureOpenWithRegistered();
        });
        // Self-heal the launch-at-startup entry against the current exe path, so a moved or
        // updated WinShot.exe still launches at login (Settings only writes it on save).
        _ = Task.Run(() => StartupRegistration.Reconcile(_settings.Current.LaunchAtStartup));
        _commandServer = new CommandServer();
        _commandServer.CommandReceived += OnCommandReceived;
        _commandServer.Start();

        SetupTray();
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        // Create the region selector's window handles once at idle so the first hotkey
        // press re-shows warm windows instead of paying 200-900 ms of window creation.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
                selector.Prewarm();
                FastRegionSelectorDialog.Return(selector);
            }
            catch (Exception ex) { Log.Error("Selector prewarm failed (non-fatal)", ex); }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // Electron apps keep renderer accessibility enabled while this mutex exists —
        // required for Alt-hover element detection to see their full element tree.
        WinShot.Capture.MsaaElementDetector.HoldScreenReaderMutex();
        Log.Info("WinShot started");
        // Ask once for capture without Windows' yellow border; the answer is cached for
        // the process and every capture session opts out on the strength of it.
        BorderlessCaptureAccess.RequestOnce();

        // Building the Windows OCR engine is a one-time cost; do it now rather than on
        // whoever presses the Extract text hotkey first. The language has to be set first
        // or the warm-up caches an engine the user did not ask for.
        OcrService.PreferredLanguage = _settings.Current.OcrLanguage;
        OcrService.Warm();

        if (_settings.Current.CheckForUpdatesOnStartup)
            _ = StartupUpdateCheckAsync();

        if (incomingCommand is not null)
            RunCommand(incomingCommand);

        // Recovery stays behind normal startup initialization and never interrupts an
        // activation that immediately opens a capture/recording/settings surface.
        Dispatcher.BeginInvoke(
            () => RunRecordingRecoveryAtStartup(startupCommandActive: incomingCommand is not null),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void RunRecordingRecoveryAtStartup(bool startupCommandActive)
    {
        if (_recordingRecovery is null)
            return;

        HistoryService history = _history;

        bool recordingActive = startupCommandActive ||
            _captureInProgress ||
            (_recording?.BlocksStartupRecovery ?? false);
        string? activeTempPath = _recording?.ActiveTempPath;

        RecordingRecoveryRunResult result;
        try
        {
            result = _recordingRecovery.Run(
                settingsReady: true,
                historyReady: true,
                recordingActive,
                activeTempPath,
                candidates =>
                {
                    var dialog = new RecordingRecoveryWindow(candidates);
                    return dialog.ShowDialog() == true
                        ? RecordingRecoveryDecision.Recover
                        : RecordingRecoveryDecision.KeepForLater;
                });
        }
        catch (Exception ex)
        {
            Log.Error("Recording startup recovery check failed; temp files were not changed", ex);
            ShowBalloon("Recording recovery", "Couldn't check unfinished recordings. Temp files were not changed.", ToastSeverity.Error);
            return;
        }

        foreach (string warning in result.HistoryWarnings)
            Log.Error($"Recovered recording was not added to History: {warning}");

        if (result.RecoveredPaths.Count > 0)
        {
            string latest = result.RecoveredPaths[^1];
            try
            {
                Action? onEdit = Path.GetExtension(latest).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? () =>
                    {
                        var editor = new VideoEditorWindow(latest, _settings, history);
                        TrackFirstRender(editor, "video editor window");
                        editor.Show();
                    }
                    : null;
                RecordingCompletionOverlay.Show(latest, _settings, onEdit);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to show recording recovery toast", ex);
                ShowBalloon("Recording recovered", Path.GetFileName(latest), ToastSeverity.Success);
            }

            if (result.RecoveredPaths.Count > 1)
                ShowBalloon("Recordings recovered", $"Recovered {result.RecoveredPaths.Count} recordings.", ToastSeverity.Success);
        }

        foreach (RecordingRecoveryFailure failure in result.Failures)
        {
            Log.Error($"Failed to recover recording; source retained at {failure.TempPath}", failure.Error);
            ThemedMessageDialog.Show(null, "Couldn't recover a recording",
                $"{failure.TempPath}\n\n" +
                "The original file is still safe at that location. Check the save folder and available space, then try again next time.\n\n" +
                FriendlyError.For(failure.Error));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Persist in-memory settings (e.g. FileNamer's counter) without re-triggering hotkey registration.
        _settings.Changed -= RegisterHotkeys;
        // A second instance shuts down before Load() ever runs — nothing to persist there.
        if (_settings.IsLoaded)
            try { _settings.Save(); } catch (Exception ex) { Log.Error("Failed to persist settings on exit", ex); }

        try { _recording?.Shutdown(); } catch (Exception ex) { Log.Error("Failed to stop recording on exit", ex); }
        try { CaptureService.ReleaseCaptureResources(); } catch (Exception ex) { Log.Error("Failed to release capture resources on exit", ex); }
        _commandServer?.Dispose();
        _hotkeys?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void SetupTray()
    {
        _tray = new WF.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Visible = true,
            Text = "WinShot",
        };

        var s = _settings.Current;
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            ShowImageMargin = false,
        };
        // Non-clickable header showing the running version — always visible at a glance.
        var versionHeader = new WF.ToolStripMenuItem(AppInfo.VersionLabel) { Enabled = false };
        menu.Items.Add(versionHeader);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Capture region", s.HotkeyCaptureRegion, () => QueueCaptureCommand("capture-area")));
        menu.Items.Add(MenuItem("Capture window", s.HotkeyCaptureWindow, () => QueueCaptureCommand("capture-window")));
        menu.Items.Add(MenuItem("Capture window with background", null, () => QueueCaptureCommand("capture-window-background")));
        menu.Items.Add(MenuItem("Capture fullscreen", s.HotkeyCaptureFullscreen, () => QueueCaptureCommand("capture-fullscreen")));
        menu.Items.Add(MenuItem("Capture fullscreen (self-timer)", null, () => QueueCaptureCommand("self-timer")));
        menu.Items.Add(MenuItem("Capture display…", null, () => QueueCaptureCommand("capture-display")));
        menu.Items.Add(MenuItem("Capture previous region", s.HotkeyCapturePrevious, () => QueueCaptureCommand("capture-previous")));
        menu.Items.Add(MenuItem("All-in-One…", s.HotkeyAllInOne, () => QueueCaptureCommand("all-in-one")));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Record screen", s.HotkeyRecord, RecordFlow));
        menu.Items.Add(MenuItem("Record display…", null, RecordDisplayFlow));
        menu.Items.Add(MenuItem("Capture text (OCR)", s.HotkeyOcr, () => QueueCaptureCommand("ocr")));
        menu.Items.Add(MenuItem("Scrolling capture", s.HotkeyScrolling, () => QueueCaptureCommand("scrolling")));
        menu.Items.Add(MenuItem("Horizontal scrolling capture", null, () => QueueCaptureCommand("scroll-horizontal")));
        menu.Items.Add(MenuItem("Scrolling capture (upward)", null, () => QueueCaptureCommand("scroll-up")));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("History…", null, OpenHistory));
        menu.Items.Add(MenuItem("Settings…", null, OpenSettings));
        menu.Items.Add(MenuItem("Pin last screenshot", s.HotkeyPinLast, PinLastFlow));
        menu.Items.Add(MenuItem("Toggle pins visibility", s.HotkeyPinToggleVisibility, TogglePinsFlow));
        menu.Items.Add(MenuItem("Close all pins", s.HotkeyPinCloseAll, ClosePinsFlow));
        menu.Items.Add(MenuItem("Unlock pinned windows", null, FastPinWindow.UnlockAllPins));
        menu.Items.Add(new WF.ToolStripSeparator());
        // Dynamic "Update available" item — hidden until a check finds a newer release.
        _updateMenuItem = MenuItem("Install update", null, () => _ = InstallPendingUpdateAsync());
        _updateMenuItem.Visible = false;
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(MenuItem("Check for updates…", null, () => _ = CheckForUpdatesFlowAsync()));
        menu.Items.Add(MenuItem("About WinShot…", null, OpenAbout));
        menu.Items.Add(MenuItem("Exit", null, Shutdown));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => QueueCaptureCommand("capture-area");
        _tray.BalloonTipClicked += (_, _) => { if (_pendingUpdate is not null) _ = InstallPendingUpdateAsync(); };
    }

    private static WF.ToolStripMenuItem MenuItem(string text, string? shortcut, Action onClick)
    {
        var item = new WF.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => onClick();
        return item;
    }

    private System.Windows.Threading.DispatcherTimer? _hotkeyRetryTimer;
    private int _hotkeyRetryCount;
    private const int MaxHotkeyRetries = 5;

    private void RegisterHotkeys() => RegisterHotkeysCore(isRetry: false);

    private void RegisterHotkeysCore(bool isRetry)
    {
        if (_hotkeys is null) return;
        if (!isRetry) _hotkeyRetryCount = 0;
        _hotkeys.UnregisterAll();
        var s = _settings.Current;
        var failed = new List<string>();
        void Reg(string gesture, Action handler)
        {
            // Empty = the user unbound this action on purpose: no hotkey registered, and NOT a
            // "failed" one (so it never triggers the "Some hotkeys unavailable" warning).
            if (string.IsNullOrWhiteSpace(gesture)) return;
            if (!_hotkeys.Register(gesture, handler)) failed.Add(gesture);
        }
        Reg(s.HotkeyCaptureRegion, () => QueueCaptureCommand("capture-area"));
        Reg(s.HotkeyCaptureWindow, () => QueueCaptureCommand("capture-window"));
        Reg(s.HotkeyCaptureFullscreen, () => QueueCaptureCommand("capture-fullscreen"));
        Reg(s.HotkeyRecord, RecordFlow);
        Reg(s.HotkeyOcr, () => QueueCaptureCommand("ocr"));
        Reg(s.HotkeyScrolling, () => QueueCaptureCommand("scrolling"));
        Reg(s.HotkeyCapturePrevious, () => QueueCaptureCommand("capture-previous"));
        Reg(s.HotkeyAllInOne, () => QueueCaptureCommand("all-in-one"));
        Reg(s.HotkeyPinToggleVisibility, TogglePinsFlow);
        Reg(s.HotkeyPinCloseAll, ClosePinsFlow);
        Reg(s.HotkeyPinLast, PinLastFlow);

        if (failed.Count == 0)
        {
            _hotkeyRetryTimer?.Stop();
            _hotkeyRetryCount = 0;
            return;
        }

        // A failure here is usually transient — e.g. a previous WinShot instance was still dying
        // (and still held the hotkey) when this one started during a silent self-update. Retry a
        // few times so the hotkeys self-heal instead of staying dead until a manual restart; only
        // warn the user once we've genuinely given up.
        if (_hotkeyRetryCount < MaxHotkeyRetries)
        {
            _hotkeyRetryCount++;
            Log.Info($"Hotkey registration incomplete ({failed.Count} failed); retry {_hotkeyRetryCount}/{MaxHotkeyRetries} in 2s");
            _hotkeyRetryTimer ??= CreateHotkeyRetryTimer();
            _hotkeyRetryTimer.Stop();
            _hotkeyRetryTimer.Start();
            return;
        }

        _hotkeyRetryTimer?.Stop();
        // Listing eight "Ctrl+Shift+1"-style gestures runs past what the toast body shows, and
        // the case that actually happens — a previous WinShot still holding them after a
        // self-update — is the long one, so the tail that got cut was the hotkeys themselves.
        // Past a couple, the count plus where to fix them is the message that fits.
        ShowBalloon(
            "Some hotkeys unavailable",
            failed.Count <= 3
                ? string.Join(", ", failed)
                : $"{failed.Count} shortcuts are in use by other apps — see Settings > Shortcuts.",
            ToastSeverity.Error);
    }

    private System.Windows.Threading.DispatcherTimer CreateHotkeyRetryTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RegisterHotkeysCore(isRetry: true);
        };
        return timer;
    }

    // ---- winshot:// command routing ----

    private void OnCommandReceived(string raw)
    {
        string? cmd = CommandServer.ParseCommand(raw);
        if (cmd is not null)
            Dispatcher.BeginInvoke(() => RunCommand(cmd));
    }

    private void RunCommand(string cmd)
    {
        if (IsCaptureCommand(cmd))
        {
            QueueCaptureCommand(cmd);
            return;
        }

        // Explorer "Open with" (or winshot edit:<path>) — open the image in the editor.
        if (cmd.StartsWith("edit:", StringComparison.OrdinalIgnoreCase))
        {
            EditFromHistory(cmd["edit:".Length..]);
            return;
        }

        switch (cmd)
        {
            case "record": RecordFlow(); break;
            case "record-display": RecordDisplayFlow(); break;
            case "history": OpenHistory(); break;
            case "settings": OpenSettings(); break;
            case "restore-last": RestoreLastFlow(); break;
            case "exit": Shutdown(); break;
        }
    }

    private static bool IsCaptureCommand(string cmd) => cmd is
        "capture-area" or "capture-window" or "capture-fullscreen" or "capture-display" or "capture-previous" or
        "capture-window-background" or
        "all-in-one" or "ocr" or "scrolling" or "scroll-horizontal" or "scroll-up" or "self-timer"
        || cmd.StartsWith("scroll-region:", StringComparison.OrdinalIgnoreCase);

    private void QueueCaptureCommand(string cmd)
    {
        // A capture flow is already active (selector open, overlay finishing): drop the
        // command instead of parking it in the pending slot. Every caller is user-initiated
        // (hotkey, tray, winshot://), and replaying a press made while the selector was
        // already open reads as a ghost re-trigger right after the user cancels with Esc.
        if (_captureInProgress || Recording.SelectionUiActive) return;
        _pendingCaptureCommand = cmd;
        SchedulePendingCaptureDrain();
    }

    private void ExecuteCaptureCommand(string cmd)
    {
        switch (cmd)
        {
            case "capture-area": CaptureRegionFlow(); break;
            case "capture-window": CaptureRegionFlow(mode: FastRegionSelectorDialog.SelectorMode.Window); break;
            case "capture-fullscreen": CaptureFullscreenFlow(); break;
            case "capture-display": CaptureDisplayFlow(); break;
            case "capture-previous": CapturePreviousFlow(); break;
            case "capture-window-background": CaptureRegionFlow(PostCaptureAction.Background, FastRegionSelectorDialog.SelectorMode.Window); break;
            case "all-in-one": AllInOneFlow(); break;
            case "ocr": OcrFlow(); break;
            case "scrolling": ScrollingFlow(); break;
            case "scroll-horizontal":
            case "scroll-up":
                ScrollingFlow(ScrollCaptureCommand.DirectionForCommand(cmd),
                    ScrollCaptureCommand.ReverseForCommand(cmd));
                break;
            case "self-timer": CaptureFullscreenTimerFlow(); break;
            // Automation hook (CleanShot's cleanshot://scrolling-capture analog):
            // scroll-region:x,y,w,h[,auto][,save] — physical screen coords, no selector.
            case var c when c.StartsWith("scroll-region:", StringComparison.OrdinalIgnoreCase):
                ScrollRegionFlow(c);
                break;
        }
    }

    /// <summary>Scripted scrolling capture over an explicit screen region — used by the
    /// automated capture tests and external automation. "auto" starts Auto-Scroll
    /// immediately; "save" writes the result silently instead of the configured action.</summary>
    private void ScrollRegionFlow(string cmd) => RunCaptureFlow(
        "Scripted scrolling capture failed", "Scrolling capture failed", async () =>
        {
            var parts = cmd["scroll-region:".Length..].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
                !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h))
            {
                ShowBalloon("Scrolling capture", "Bad scroll-region command (need x,y,w,h).", ToastSeverity.Error);
                return;
            }
            bool auto = parts.Contains("auto", StringComparer.OrdinalIgnoreCase);
            bool save = parts.Contains("save", StringComparer.OrdinalIgnoreCase);
            TimeSpan? stop = null;
            foreach (var p in parts)
            {
                if (p.StartsWith("stop=", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[5..], out int s))
                    stop = TimeSpan.FromSeconds(s);
            }

            var region = new SD.Rectangle(x, y, w, h);
            // "probe" opts the rig into the same pane scoping the interactive flow gets;
            // default stays deterministic for the existing automated tests.
            bool paneScoped = false;
            if (parts.Contains("probe", StringComparer.OrdinalIgnoreCase))
            {
                var requested = region;
                region = await Task.Run(() =>
                {
                    var center = new SD.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
                    PaneInfo? hint = ScrollPaneDetector.FromPoint(center, TimeSpan.FromMilliseconds(700));
                    return ScrollProbe.Refine(region, hint);
                });
                paneScoped = region != requested;
            }

            var (stitched, cancelled) = await ScrollingStatusWindow.Run(region, null, autoStart: auto, autoStop: stop, paneScoped: paneScoped);
            if (stitched is not null)
                HandleCapture(stitched, save ? PostCaptureAction.Save : null, HistoryCaptureKind.Scrolling);
            else if (!cancelled)
                ShowBalloon("Scrolling capture", "Nothing captured.", ToastSeverity.Error);
        });

    private void FinishCaptureFlow()
    {
        _captureInProgress = false;
        MemoryCleanup.Request();
        SchedulePendingCaptureDrain();
    }

    /// <summary>
    /// Shared envelope for every capture flow: the single-capture-in-progress guard, one
    /// failure funnel (log + balloon), and the guaranteed FinishCaptureFlow release. The
    /// guard runs synchronously before the first await, so a second trigger while a capture
    /// is active is dropped exactly as before. Fire-and-forget (async void) to match the
    /// hotkey/tray/pipe callers; exceptions surface through <paramref name="body"/>'s Task
    /// and are caught here rather than lost to the sync context.
    /// </summary>
    private async void RunCaptureFlow(string failureLog, string failureBalloon, Func<Task> body)
    {
        // The recording chooser/selector counts as an active capture UI: letting a second
        // full-screen selector open on top made the two overlays z-order-fight at 66 fps.
        if (_captureInProgress || Recording.SelectionUiActive) return;
        _captureInProgress = true;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Log.Error(failureLog, ex);
            ShowBalloon(failureBalloon, FriendlyError.For(ex), ToastSeverity.Error);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private void SchedulePendingCaptureDrain()
    {
        if (_pendingCaptureDrainActive) return;
        _pendingCaptureDrainActive = true;
        _ = DrainPendingCaptureCommandAsync();
    }

    private async Task DrainPendingCaptureCommandAsync()
    {
        try
        {
            while (_pendingCaptureCommand is not null)
            {
                if (_captureInProgress)
                {
                    // A flow started between this command being queued and the drain
                    // reaching it (rapid double-press pumped before the first flow set
                    // _captureInProgress): drop it, same intent as the guard in
                    // QueueCaptureCommand — a press made while a capture is active must
                    // never replay after the user cancels.
                    _pendingCaptureCommand = null;
                    return;
                }

                string cmd = _pendingCaptureCommand;
                _pendingCaptureCommand = null;
                await Dispatcher.InvokeAsync(() => ExecuteCaptureCommand(cmd));
            }
        }
        finally
        {
            _pendingCaptureDrainActive = false;
            if (_pendingCaptureCommand is not null && !_captureInProgress)
                SchedulePendingCaptureDrain();
        }
    }

    // ---- Capture flows ----

    private void CaptureRegionFlow(
        string? postCaptureActionOverride = null,
        FastRegionSelectorDialog.SelectorMode mode = FastRegionSelectorDialog.SelectorMode.Area) => RunCaptureFlow(
        "Region capture failed", "Capture failed", async () =>
        {
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            PerfLog.TrackFirstShown(selector, mode == FastRegionSelectorDialog.SelectorMode.Window ? "capture-window selector" : "capture-area selector");
            try
            {
                if (await selector.ShowAsync(mode, elementHover: mode == FastRegionSelectorDialog.SelectorMode.Area) == WF.DialogResult.OK &&
                    selector.SelectedRegionPx is SD.Rectangle region)
                {
                    // Screen-freeze: the selector already cropped the result from its frozen
                    // snapshot, so use that (exact, no dismiss delay). Fall back to a live grab.
                    SD.Bitmap? frozenCrop = selector.TakeCapturedRegion();
                    if (frozenCrop is null)
                    {
                        await WaitForOverlayDismissAsync();
                        frozenCrop = await CaptureSelectedRegionAsync(region, "capture-area");
                    }
                    HandleCapture(frozenCrop, postCaptureActionOverride);
                }
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }
        });

    private void CaptureFullscreenFlow() => RunCaptureFlow(
        "Fullscreen capture failed", "Capture failed", async () =>
        {
            var sw = Stopwatch.StartNew();
            var capture = await RunCaptureWorkAsync(CaptureDesktopRespectingSettings);
            LogPerf("capture-fullscreen screen grab", sw);
            HandleCapture(capture);
        });

    private void CaptureFullscreenTimerFlow() => RunCaptureFlow(
        "Self-timer capture failed", "Capture failed", async () =>
        {
            await FastSelfTimerWindow.RunAsync(_settings.Current.SelfTimerSeconds);
            var sw = Stopwatch.StartNew();
            var capture = await RunCaptureWorkAsync(CaptureDesktopRespectingSettings);
            LogPerf("self-timer screen grab", sw);
            HandleCapture(capture);
        });

    private void CaptureDisplayFlow() => RunCaptureFlow(
        "Display capture failed", "Capture failed", async () =>
        {
            if (FastDisplayPickerDialog.ChooseDisplays() is { Length: > 0 } picked)
            {
                // Multiple picked displays screenshot as the union rectangle.
                SD.Rectangle r = picked.Aggregate(SD.Rectangle.Union);
                var sw = Stopwatch.StartNew();
                var capture = await RunCaptureWorkAsync(() => CaptureScreenRegionRespectingSettings(r));
                LogPerf("capture-display screen grab", sw);
                HandleCapture(capture);
            }
        });

    private void CapturePreviousFlow() => RunCaptureFlow(
        "Capture-previous failed", "Capture failed", async () =>
        {
            if (PreviousRegion.TryParse(_settings.Current.LastCaptureRegion, out SD.Rectangle r))
            {
                var sw = Stopwatch.StartNew();
                var capture = await RunCaptureWorkAsync(() => CaptureScreenRegionRespectingSettings(r));
                LogPerf("capture-previous screen grab", sw);
                HandleCapture(capture);
            }
            else
                // Info, not Error: nothing went wrong, a prerequisite is simply not met yet —
                // the same shape as OCR's "Nothing found", which for the same reason does not
                // wear a red glyph. The long dwell is what gives the sentence time to read.
                ShowBalloon("Capture previous region", "No previous region yet — capture a region first.", ToastSeverity.Info, holdLong: true);
        });

    private void AllInOneFlow() => RunCaptureFlow(
        "All-in-one capture failed", "Capture failed", async () =>
        {
            AllInOneAction action;
            SD.Rectangle regionPx;
            SD.Bitmap? captured = null;

            var selector = FastAllInOneSelectorDialog.Rent(CreateWindowListTask, _settings);
            try
            {
                PerfLog.TrackFirstShown(selector, "all-in-one selector");
                if (await selector.ShowAsync() != WF.DialogResult.OK || selector.SelectedRegionPx is not SD.Rectangle rp)
                    return;

                action = selector.SelectedAction;
                regionPx = rp;
                // Screen-freeze: prefer the crop from the frozen snapshot the user framed
                // against (same as the region selector) — must be taken before Return()
                // disposes it. Fall back to a live grab below if the freeze wasn't available.
                if (action is AllInOneAction.Capture or AllInOneAction.Ocr)
                    captured = selector.TakeCapturedRegion();
            }
            finally
            {
                FastAllInOneSelectorDialog.Return(selector);
            }

            if (action is AllInOneAction.Scroll || (captured is null && action is AllInOneAction.Capture or AllInOneAction.Ocr))
                await WaitForOverlayDismissAsync();

            if (captured is null && action is AllInOneAction.Capture or AllInOneAction.Ocr)
                captured = await CaptureSelectedRegionAsync(regionPx, $"all-in-one {action.ToString().ToLowerInvariant()}");

            switch (action)
            {
                case AllInOneAction.Capture:
                    HandleCapture(captured!);
                    break;
                case AllInOneAction.Ocr:
                    using (captured) await RunOcrToClipboard(captured!);
                    break;
                case AllInOneAction.Record:
                    Recording.ToggleFlowWithRegion(regionPx);
                    break;
                case AllInOneAction.Scroll:
                    var region = regionPx;
                    region.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);
                    var (stitched, cancelled) = await ScrollingStatusWindow.Run(region);
                    if (stitched is not null)
                        HandleCapture(stitched, historyKind: HistoryCaptureKind.Scrolling);
                    else if (!cancelled)
                        ShowBalloon("Scrolling capture", "Nothing captured.", ToastSeverity.Error);
                    break;
            }
        });

    /// <summary>Captures the virtual desktop, optionally hiding desktop icons first per settings.</summary>
    private SD.Bitmap CaptureDesktopRespectingSettings() =>
        DesktopIconCaptureGuard.Run(
            _settings.Current.HideDesktopIconsDuringCapture,
            () => DesktopIcons.Visible,
            DesktopIcons.Hide,
            DesktopIcons.Show,
            Thread.Sleep,
            CaptureService.CaptureVirtualDesktop);

    private SD.Bitmap CaptureScreenRegionRespectingSettings(SD.Rectangle screenRect) =>
        DesktopIconCaptureGuard.Run(
            _settings.Current.HideDesktopIconsDuringCapture,
            () => DesktopIcons.Visible,
            DesktopIcons.Hide,
            DesktopIcons.Show,
            Thread.Sleep,
            () => CaptureService.CaptureScreenRegion(screenRect));

    private async Task<SD.Bitmap> CaptureSelectedRegionAsync(SD.Rectangle virtualRegion, string metricName)
    {
        var screenRegion = virtualRegion;
        screenRegion.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);
        var sw = Stopwatch.StartNew();
        try
        {
            return await RunCaptureWorkAsync(() => CaptureScreenRegionRespectingSettings(screenRegion));
        }
        finally
        {
            LogPerf($"{metricName} screen grab", sw);
        }
    }

    private static Task WaitForOverlayDismissAsync() => Task.Delay(OverlayDismissDelayMs);

    private static Task<List<WindowInfo>> CreateWindowListTask() =>
        Task.Run(() => WindowEnumerator.GetTopLevelWindows());

    private static void TrackFirstRender(Window window, string metricName)
    {
        var sw = Stopwatch.StartNew();
        bool logged = false;
        EventHandler? handler = null;
        void LogOnce()
        {
            if (logged) return;
            logged = true;
            if (handler is not null)
                window.ContentRendered -= handler;
            LogPerf($"{metricName} first render", sw);
        }
        handler = (_, _) => LogOnce();
        window.ContentRendered += handler;
        window.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(LogOnce));
    }

    private static void LogPerf(string metricName, Stopwatch sw) =>
        Log.Info($"Perf {metricName}: {sw.ElapsedMilliseconds} ms");

    private static void LogOverlayPerf(Stopwatch sw, long createMs, long wireMs, long showMs)
    {
        if (sw.ElapsedMilliseconds > 50)
            Log.Info($"Perf quick actions overlay breakdown: create={createMs} wire={wireMs} show={showMs} total={sw.ElapsedMilliseconds} ms");
    }

    private static Task<T> RunCaptureWorkAsync<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Every captured bitmap funnels through here: optional downscale, history, then the post-capture action.</summary>
    private void HandleCapture(
        SD.Bitmap bmp,
        string? postCaptureActionOverride = null,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular)
    {
        if (_settings.Current.DownscaleHiDpi)
        {
            if (HiDpiDownscale.TryGetTargetSize(bmp.Width, bmp.Height, SystemDpiScale, out int w, out int h))
            {
                try
                {
                    var scaled = BitmapEffects.Resize(bmp, w, h);
                    bmp.Dispose();
                    bmp = scaled;
                }
                catch (Exception ex) { Log.Error("HiDPI downscale failed", ex); }
            }
        }

        string action = PostCaptureAction.Normalize(postCaptureActionOverride ?? _settings.Current.PostCaptureAction);
        if (!PostCaptureAction.IsDirectAction(action))
        {
            try { ShowOverlayWithDeferredCaptureWork(bmp, autoCopy: _settings.Current.AutoCopyToClipboard, historyKind); }
            catch { bmp.Dispose(); throw; }
            return;
        }

        if (action == PostCaptureAction.Edit)
        {
            OpenDirectEditCapture(bmp, historyKind);
            return;
        }

        _ = AddHistoryAsync(
            bmp,
            cloneOnCallerThread: PostCaptureAction.NeedsCallerThreadHistoryClone(action),
            historyKind);
        // Direct actions below take ownership of bmp (Save disposes it, Pin/Background
        // hand it to a window), and the auto-copy clones on its OWN STA thread — sharing
        // bmp raced use-against-dispose (save failing "Parameter is not valid", or the
        // copy dying and arming the 8s auto-copy suppression). Clone on this thread so
        // the copy owns its bitmap outright, mirroring the history path.
        if (_settings.Current.AutoCopyToClipboard && action != PostCaptureAction.Copy)
            _ = QueueAutoClipboardCopy(CaptureService.CloneBitmap(bmp), takeOwnership: true);

        switch (action)
        {
            case PostCaptureAction.Copy:
                _ = CopyToClipboardAndNotifyAsync(bmp, takeOwnership: true, showSuccess: true, showFailure: true, includePng: true, failureContext: "Copy failed");
                break;
            case PostCaptureAction.Save:
                SaveSilently(bmp); // takes ownership
                break;
            case PostCaptureAction.Pin:
                try
                {
                    ShowPin(bmp);
                }
                catch { bmp.Dispose(); throw; }
                break;
            case PostCaptureAction.Background:
                try
                {
                    var win = new BackgroundComposerWindow(bmp, _settings, _history);
                    TrackFirstRender(win, "background window");
                    win.Show();
                }
                catch { bmp.Dispose(); throw; }
                break;
        }
    }

    private void OpenDirectEditCapture(SD.Bitmap bmp, HistoryCaptureKind historyKind)
    {
        DirectEditCaptureTestHooks? hooks = DirectEditTestHooks;
        Func<SD.Bitmap, Task> historyWork = hooks?.HistoryWork ?? (source =>
            Task.Run(() =>
            {
                try { _history.Add(source, historyKind); }
                catch (Exception ex) { Log.Error("Failed to add direct Edit capture to history", ex); }
            }));
        Func<SD.Bitmap, Task>? autoCopyWork = _settings.Current.AutoCopyToClipboard
            ? hooks?.AutoCopyWork ?? CopyBorrowedCaptureToClipboardAsync
            : null;
        var handoff = new DirectEditCaptureHandoff(bmp, historyWork, autoCopyWork);

        try
        {
            var win = EditorWindow.CreateForDirectCapture(bmp, _settings, _history, handoff.RunAsync);
            hooks?.ConfigureEditor?.Invoke(win);
            TrackFirstRender(win, "editor window");
            win.Show();
            hooks?.EditorShown?.Invoke(win);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    internal void HandleCaptureForTest(
        SD.Bitmap bmp,
        string? postCaptureActionOverride = null,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular) =>
        HandleCapture(bmp, postCaptureActionOverride, historyKind);

    internal void SetHistoryForTest(HistoryService history) => _history = history;

    private Task<string?> AddHistoryAsync(
        SD.Bitmap bmp,
        bool cloneOnCallerThread,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular)
    {
        if (cloneOnCallerThread)
        {
            try
            {
                return AddHistoryCopyAsync(CaptureService.CloneBitmap(bmp), historyKind);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to clone capture for history", ex);
                return Task.FromResult<string?>(null);
            }
        }

        return Task.Run(() =>
        {
            try
            {
                using var copy = CaptureService.CloneBitmap(bmp);
                return _history.Add(copy, historyKind);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add capture to history", ex);
                return null;
            }
        });
    }

    private Task<string?> AddHistoryCopyAsync(
        SD.Bitmap copy,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular)
    {
        return Task.Run(() =>
        {
            using (copy)
            {
                try { return _history.Add(copy, historyKind); }
                catch (Exception ex)
                {
                    Log.Error("Failed to add capture to history", ex);
                    return null;
                }
            }
        });
    }

    private RecordingController Recording
    {
        get
        {
            if (_recording is not null)
                return _recording;
            _recording = new RecordingController(_settings, _history);
            _recording.RecordingElapsedChanged += OnRecordingElapsedChanged;
            return _recording;
        }
    }

    private void OnRecordingElapsedChanged(TimeSpan? elapsed)
    {
        if (_tray is null)
            return;
        _tray.Text = elapsed is null
            ? "WinShot"
            : $"WinShot — Recording {(int)elapsed.Value.TotalMinutes:00}:{elapsed.Value.Seconds:00}";
    }

    private Task QueueAutoClipboardCopy(SD.Bitmap bmp, bool takeOwnership = false)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _autoCopySuppressedUntilTick))
        {
            if (takeOwnership)
                bmp.Dispose();
            Log.Info("Auto-copy to clipboard skipped: clipboard temporarily unavailable.");
            return Task.CompletedTask;
        }

        return CopyToClipboardAndNotifyAsync(
            bmp,
            takeOwnership: takeOwnership,
            showSuccess: false,
            showFailure: false,
            includePng: false,
            failureContext: "Auto-copy to clipboard skipped",
            isAutoCopy: true);
    }

    private async Task CopyBorrowedCaptureToClipboardAsync(SD.Bitmap bmp)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _autoCopySuppressedUntilTick))
        {
            Log.Info("Auto-copy to clipboard skipped: clipboard temporarily unavailable.");
            return;
        }

        try
        {
            await CaptureService.CopyToClipboardBorrowedAsync(bmp, includePng: false);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(
                ref _autoCopySuppressedUntilTick,
                Environment.TickCount64 + AutoCopyFailureCooldownMs);
            Log.Info($"Auto-copy to clipboard skipped: {ex.Message}");
        }
    }

    private async Task CopyToClipboardAndNotifyAsync(
        SD.Bitmap bmp,
        bool takeOwnership,
        bool showSuccess,
        bool showFailure,
        bool includePng,
        string failureContext,
        bool isAutoCopy = false)
    {
        try
        {
            await CaptureService.CopyToClipboardAsync(bmp, takeOwnership, includePng);
            if (showSuccess)
                await Dispatcher.InvokeAsync(() => ShowBalloon("Copied", "The capture is on the clipboard.", ToastSeverity.Success));
        }
        catch (Exception ex)
        {
            if (showFailure)
            {
                Log.Error(failureContext, ex);
                await Dispatcher.InvokeAsync(() => ShowBalloon("Copy failed", FriendlyError.For(ex), ToastSeverity.Error));
            }
            else
            {
                if (isAutoCopy)
                    Interlocked.Exchange(
                        ref _autoCopySuppressedUntilTick,
                        Environment.TickCount64 + AutoCopyFailureCooldownMs);
                Log.Info($"{failureContext}: {ex.Message}");
            }
        }
    }

    private void ShowOverlayWithDeferredCaptureWork(
        SD.Bitmap bmp,
        bool autoCopy,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular)
    {
        var sw = Stopwatch.StartNew();
        var historyPathSource = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captureWorkComplete = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = FastQuickActionsWindow.CreateWithDeferredImageRelease(
            bmp,
            _settings,
            historyPathSource.Task,
            captureWorkComplete.Task);
        long createMs = sw.ElapsedMilliseconds;
        bool started = false;
        PerfLog.TrackFirstShown(overlay, "quick actions overlay");
        overlay.Shown += (_, _) =>
        {
            if (started) return;
            started = true;
            overlay.BeginInvoke(new Action(() =>
                _ = RunDeferredOverlayCaptureWorkAsync(bmp, historyPathSource, captureWorkComplete, autoCopy, historyKind)));
        };
        WireOverlay(overlay);
        long wireMs = sw.ElapsedMilliseconds - createMs;
        overlay.Show();
        long showMs = sw.ElapsedMilliseconds - createMs - wireMs;
        LogOverlayPerf(sw, createMs, wireMs, showMs);
    }

    private async Task RunDeferredOverlayCaptureWorkAsync(
        SD.Bitmap bmp,
        TaskCompletionSource<string?> historyPathCompletion,
        TaskCompletionSource<object?> captureWorkCompletion,
        bool autoCopy,
        HistoryCaptureKind historyKind)
    {
        try
        {
            string? historyPath = await Task.Run(() =>
            {
                try
                {
                    using var copy = CaptureService.CloneBitmap(bmp);
                    return _history.Add(copy, historyKind);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to add capture to history", ex);
                    return null;
                }
            }).ConfigureAwait(false);
            historyPathCompletion.TrySetResult(historyPath);

            // Await the copy: captureWorkCompletion is the overlay's gate for disposing
            // bmp, and the copy's clone happens on its own STA thread — completing the
            // gate with the copy still in flight let a quickly-dismissed overlay dispose
            // bmp under the clone (auto-copy silently died + armed the 8s suppression).
            if (autoCopy)
                await QueueAutoClipboardCopy(bmp).ConfigureAwait(false);

            captureWorkCompletion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            Log.Error("Deferred overlay capture work failed", ex);
            historyPathCompletion.TrySetResult(null);
        }
        finally
        {
            captureWorkCompletion.TrySetResult(null);
            MemoryCleanup.Request();
        }
    }

    private void ShowOverlay(SD.Bitmap bmp, string? historyPath)
    {
        var sw = Stopwatch.StartNew();
        var overlay = new FastQuickActionsWindow(bmp, _settings, historyPath);
        long createMs = sw.ElapsedMilliseconds;
        PerfLog.TrackFirstShown(overlay, "quick actions overlay");
        WireOverlay(overlay);
        long wireMs = sw.ElapsedMilliseconds - createMs;
        overlay.Show();
        long showMs = sw.ElapsedMilliseconds - createMs - wireMs;
        LogOverlayPerf(sw, createMs, wireMs, showMs);
    }

    private void WireOverlay(FastQuickActionsWindow overlay)
    {
        overlay.EditRequested += OpenEditorFromOverlay;
        overlay.PinRequested += PinFromOverlay;
        overlay.OcrRequested += OcrFromOverlay;
        overlay.BackgroundRequested += BackgroundFromOverlay;
    }

    private void SaveSilently(SD.Bitmap bmp)
    {
        string path;
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            path = FileNamer.NextUniquePath(_settings, _settings.Current.SaveFolder, _settings.Current.ImageFormat);
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            Log.Error("Could not choose a save path", ex);
            ShowBalloon("Save failed", FriendlyError.For(ex), ToastSeverity.Error);
            return;
        }

        _ = Task.Run(() =>
        {
            using (bmp)
            {
                try
                {
                    ImageSaver.Save(bmp, path);
                    Dispatcher.InvokeAsync(() => ShowBalloon("Saved", Path.GetFileName(path), ToastSeverity.Success));
                }
                catch (Exception ex)
                {
                    Log.Error("Silent save failed", ex);
                    Dispatcher.InvokeAsync(() => ShowBalloon("Save failed", FriendlyError.For(ex), ToastSeverity.Error));
                }
            }
        });
    }

    // ---- Feature entry points ----

    // Toggling a recording is legal while one is running (that's the stop path), but a
    // screenshot selector being open means its overlay would bury/battle the record UI.
    private void RecordFlow()
    {
        if (_captureInProgress) return;
        Recording.ToggleFlow();
    }

    private void RecordDisplayFlow()
    {
        if (_captureInProgress) return;
        Recording.ToggleDisplayFlow();
    }

    private void OcrFlow() => RunCaptureFlow(
        "OCR capture failed", "OCR failed", async () =>
        {
            SD.Bitmap? crop = null;
            SD.Point? anchor = null;
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            PerfLog.TrackFirstShown(selector, "ocr selector");
            try
            {
                if (await selector.ShowAsync() == WF.DialogResult.OK && selector.SelectedRegionPx is SD.Rectangle region)
                {
                    // Screen center of the selection, so the confirmation HUD pops over it.
                    var vs = CaptureService.VirtualScreen;
                    anchor = new SD.Point(vs.X + region.X + region.Width / 2, vs.Y + region.Y + region.Height / 2);
                    // Prefer the frozen-snapshot crop; fall back to a live grab.
                    crop = selector.TakeCapturedRegion();
                    if (crop is null)
                    {
                        await WaitForOverlayDismissAsync();
                        crop = await CaptureSelectedRegionAsync(region, "ocr");
                    }
                }
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }
            if (crop is null) return;
            using (crop)
                await RunOcrToClipboard(crop, anchor);
        });

    private async Task RunOcrToClipboard(
        SD.Bitmap bmp,
        SD.Point? anchor = null,
        FastQuickActionsWindow? card = null)
    {
        OcrCaptureResult result;
        ToastWindow? working = null;
        try
        {
            var sw = Stopwatch.StartNew();
            // OcrUnavailableException is the one exception in this flow whose message is
            // written for the user, so it is the one allowed on screen verbatim. Catching
            // InvalidOperationException instead caught the type and not the message: any
            // framework InvalidOperationException from the SoftwareBitmap/GDI+ work inside
            // ExtractAsync reached the user as raw exception text.
            Task<OcrCaptureResult> extraction = OcrService.ExtractAsync(bmp, _settings.Current.OcrJoinLines);

            // The selector is already gone and the result toast is not up yet, so a slow read
            // used to happen against a completely unchanged screen. Only a read the user
            // would actually notice is announced: a warm engine on a small crop finishes
            // inside the delay and says nothing at all.
            if (await Task.WhenAny(extraction, Task.Delay(WorkingIndicatorDelayMs)) != extraction)
            {
                // Sticky, not holdLong: a read slow enough to have earned an indicator can
                // outlive 4.5 s (a cold engine on a big crop), and counting down would put
                // the screen back to saying nothing with the work still in flight. Every
                // exit below either closes this toast or replaces it with the result.
                working = ShowOcrToast("Reading text", "Working on the selection.",
                    anchor ?? CardAnchor(card), onOpen: null, ToastSeverity.Info,
                    extras: new ToastExtras { Sticky = true });
            }

            result = await extraction;
            LogPerf("ocr extract", sw);
        }
        catch (OcrUnavailableException ex)
        {
            Log.Info($"OCR unavailable: {ex.Message}");
            ShowBalloon("OCR unavailable", ex.Message, ToastSeverity.Error);
            return;
        }

        try
        {
            OcrClipboardPayload? payload = OcrClipboardFormatter.Build(result);
            if (payload is null)
            {
                if (card is not null)
                {
                    // The card's status flash is the feedback here, so nothing replaces the
                    // working toast; close it rather than leave it dwelling over the answer.
                    CloseWorkingToast(working);
                    card.FlashStatus("No text found");
                }
                // A toast result replaces the working one; a null return means the toast could
                // not be built and the shell balloon answered instead, so nothing replaced it
                // and it would dwell for its full 30 s backstop over an answer already given.
                else if (ShowOcrToast(
                             "Nothing found", "No text or codes in the selection.", anchor,
                             onOpen: null, ToastSeverity.Info, holdLong: true) is null)
                    CloseWorkingToast(working);
                return;
            }

            await CaptureService.SetTextToClipboardAsync(payload.ClipboardText);
            Action? open = BuildOpenAction(payload.ClipboardText);
            if (card is not null && open is null)
            {
                // "Text copied" was hard-coded here, so a QR code run from the card was
                // announced as text. The formatter already knows which it was.
                CloseWorkingToast(working);
                card.FlashStatus(payload.BalloonTitle);
            }
            else
            {
                // A decoded link is a decision, not a confirmation: the card's status flash
                // has nowhere to put an Open button, so the toast comes up over the card.
                if (ShowOcrToast(payload.BalloonTitle, payload.Preview,
                        anchor ?? CardAnchor(card), open,
                        extras: open is not null ? LinkExtras() : LineBreakExtras(result)) is null)
                    CloseWorkingToast(working);
            }
        }
        catch (COMException ex)
        {
            Log.Info($"OCR clipboard copy skipped: {ex.Message}");
            // One condition reads one way across surfaces: this used to name the state
            // ("the clipboard is busy") without saying what to do about it, while every
            // other copy path said to try again in a moment.
            ShowBalloon("Copy failed", FriendlyError.For(ex), ToastSeverity.Error);
        }
    }

    /// <summary>How long a read has to run before it is worth telling the user it is
    /// running. Under this, the result lands before an indicator could be read.</summary>
    private const int WorkingIndicatorDelayMs = 220;

    /// <summary>Takes the working state down when the result is not itself a toast (the card
    /// paths). A toast result replaces it on its own -- that is what showing one does.</summary>
    private static void CloseWorkingToast(ToastWindow? working)
    {
        if (working is null || working.IsDisposed) return;
        try { working.Close(); }
        catch (Exception ex) { Log.Error("Could not close the OCR working toast (non-fatal)", ex); }
    }

    /// <summary>
    /// Opening a link is a decision, so the toast waits for one: an explicit Don't open
    /// beside Open link, and a checkbox that stops the offer for good. It used to be a 4.5 s
    /// window with no way to decline and no way to say "stop asking".
    ///
    /// Declared deviation from §4.6, whose Fix column names the decline button "Dismiss":
    /// against a primary reading "Open link", "Don't open" says what declining does instead of
    /// naming the gesture, which is the sentence-case, consequence-phrased copy the rubric asks
    /// for. The behaviour the Fix column specifies -- a real second answer, not just a timeout
    /// -- is unchanged.
    /// </summary>
    private ToastExtras LinkExtras() => new()
    {
        Sticky = true,
        SecondaryText = "Don't open",
        CheckboxText = "Don't detect links",
        CheckboxChanged = dontDetect =>
        {
            _settings.Current.OcrDetectLinks = !dontDetect;
            // Quietly: Save() raises Changed, which unregisters and re-registers all eleven
            // global hotkeys. A tick on a toast checkbox must not risk "Some hotkeys
            // unavailable" because another app grabbed a gesture in the gap, and nothing
            // subscribes to OcrDetectLinks anyway.
            _settings.SaveQuietly();
        },
    };

    /// <summary>
    /// Line joining is a preference you can only judge after seeing the text, so the toast
    /// offers the other answer in place and re-copies. The formatter is pure and takes the
    /// flag as an argument, so this is the same call with it flipped.
    /// </summary>
    private ToastExtras? LineBreakExtras(OcrCaptureResult result)
    {
        // Nothing to join: one line either way, and a button that does nothing is worse
        // than no button.
        if (result.Lines.Count < 2) return null;

        bool joined = _settings.Current.OcrJoinLines;
        return new ToastExtras
        {
            ToggleText = joined ? "Add breaks" : "Cut breaks",
            Toggle = async () =>
            {
                joined = !joined;
                // Awaited, and through the same Combine the original copy used: a discarded
                // Task hid a busy clipboard behind a button that relabelled itself anyway,
                // and reformatting the text alone dropped any decoded code from underneath it.
                string text = OcrClipboardFormatter.Combine(
                    OcrTextFormatter.Format(result.Lines, joined), result.QrCodes);
                await CaptureService.SetTextToClipboardAsync(text);
                return joined ? "Add breaks" : "Cut breaks";
            },
        };
    }

    /// <summary>
    /// The top edge of the quick-actions card, so a toast raised from the card path lands
    /// over the capture it is about instead of in the tray corner. The card's midpoint would
    /// be wrong: ToastWindow places an anchored toast with its *bottom* just above the anchor,
    /// so anchoring to the centre parks a TopMost window across the card's upper band -- and a
    /// sticky link toast would sit there until clicked, hiding the thumbnail and swallowing
    /// the first click aimed at Copy or Edit. That is the overlap
    /// <see cref="ToastWindow.CornerPlacement"/> exists to prevent.
    /// </summary>
    private static SD.Point? CardAnchor(FastQuickActionsWindow? card)
    {
        if (card is null || card.IsDisposed) return null;
        SD.Rectangle bounds = card.Bounds;
        return new SD.Point(bounds.Left + (bounds.Width / 2), bounds.Top);
    }

    /// <summary>Returns an Open action when the copied text is a single http(s) URL
    /// (e.g. a decoded QR code) and the user still wants links detected, else null.</summary>
    private Action? BuildOpenAction(string? clipboardText)
    {
        if (OcrLink.Detect(clipboardText, _settings.Current.OcrDetectLinks) is not Uri uri)
            return null;
        return () => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Severity is per-call: "Nothing found" is not a success, and a checkmark on it
    /// reads as "it worked".</summary>
    private ToastWindow? ShowOcrToast(
        string title, string preview, SD.Point? anchor, Action? onOpen,
        ToastSeverity severity = ToastSeverity.Success, bool holdLong = false,
        ToastExtras? extras = null) =>
        ShowToast(title, preview, anchor, onOpen, severity, holdLong, extras);

    private void ScrollingFlow(ScrollDirection? direction = null, bool reverse = false) => RunCaptureFlow(
        "Scrolling capture failed", "Scrolling capture failed", async () =>
        {
            SD.Rectangle? picked = null;
            bool paneClick = false;
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            PerfLog.TrackFirstShown(selector, "scrolling selector");
            try
            {
                if (await selector.ShowAsync(paneHover: true,
                        instruction: reverse
                            ? "Drag over the part that scrolls, then scroll up"
                            : "Drag over the part of the screen that scrolls") == WF.DialogResult.OK &&
                    selector.SelectedRegionPx is SD.Rectangle r)
                {
                    picked = r;
                    paneClick = selector.SelectedByPaneClick;
                }
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }
            if (picked is not SD.Rectangle region) return;

            await WaitForOverlayDismissAsync();
            region.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);

            // Pane scoping runs ONLY for pane clicks: the highlighted-pane rect is a hint,
            // so the wheel probe verifies and tightens it. A hand-drawn marquee is an
            // explicit instruction — captured exactly as drawn, no second-guessing.
            SD.Rectangle refined = region;
            if (paneClick)
            {
                refined = await Task.Run(() =>
                {
                    var center = new SD.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
                    PaneInfo? hint = ScrollPaneDetector.FromPoint(center, TimeSpan.FromMilliseconds(700));
                    return ScrollProbe.Refine(region, hint);
                });
            }

            // Escape and Cancel are the user finishing the flow the way they chose to; only a
            // run that came back with nothing to show for it is a failure worth a red card.
            var (stitched, cancelled) = await ScrollingStatusWindow.Run(refined, direction, paneScoped: paneClick, reverse: reverse);
            if (stitched is not null)
                HandleCapture(stitched, historyKind: HistoryCaptureKind.Scrolling);
            else if (!cancelled)
                ShowBalloon("Scrolling capture", "Nothing captured.", ToastSeverity.Error);
        });

    private async void RestoreLastFlow()
    {
        string? path = FastQuickActionsWindow.PopRecentlyClosed();
        if (path is null)
        {
            // Prerequisite not met, not a failure — see CapturePreviousFlow.
            ShowBalloon("Restore", "No recently closed capture to restore.", ToastSeverity.Info, holdLong: true);
            return;
        }
        if (await LoadBitmapCopyAsync(path) is SD.Bitmap bmp)
            ShowOverlay(bmp, path);
    }

    private void OpenHistory()
    {
        var sw = Stopwatch.StartNew();
        var win = HistoryWindow.Show(_history, _settings);
        LogPerf("history window show", sw);
        win.EditRequested -= EditFromHistory;
        win.EditRequested += EditFromHistory;
        win.PinRequested -= PinFromHistory;
        win.PinRequested += PinFromHistory;
        win.RestoreRequested -= RestoreFromHistory;
        win.RestoreRequested += RestoreFromHistory;
        win.MultiPinRequested -= PinMultipleFromHistory;
        win.MultiPinRequested += PinMultipleFromHistory;
    }

    private void OpenSettings()
    {
        var sw = Stopwatch.StartNew();
        SettingsWindow.Show(_settings);
        LogPerf("settings window show", sw);
    }

    private void OpenAbout() => SettingsWindow.Show(_settings).SelectAboutTab();

    private void OpenEditorFromOverlay(FastQuickActionsWindow overlay)
    {
        var win = EditorWindow.CreateForCapture(overlay.CloneImage(), _settings, _history);
        TrackFirstRender(win, "editor window");
        win.Show();
        overlay.Close();
    }

    /// <summary>
    /// Hides or restores every pin. Both this and <see cref="ClosePinsFlow"/> are bound to a
    /// global hotkey and a tray row that are always enabled, and both used to return in
    /// silence with no pin on screen -- indistinguishable from a hotkey that never registered.
    /// The sibling flow already says "No capture in history to pin."; these say the same kind
    /// of thing.
    /// </summary>
    private void TogglePinsFlow() => PinCommand(FastPinWindow.ToggleAllVisibility);

    private void ClosePinsFlow() => PinCommand(FastPinWindow.CloseAll);

    private void PinCommand(Action command)
    {
        if (!FastPinWindow.AnyOpen)
        {
            ShowBalloon("Pins", "No pinned screenshots on screen.", ToastSeverity.Info, holdLong: true);
            return;
        }

        command();
    }

    /// <summary>
    /// Pins the newest capture in history. The point is to get a reference back on screen
    /// without opening History and hunting for it, so an empty history is a prerequisite not
    /// met rather than a failure -- the same shape as "No previous region yet".
    /// </summary>
    private async void PinLastFlow()
    {
        string? path;
        try
        {
            // Off the UI thread: GetItems enumerates the history folder, and a five-thousand
            // file history would otherwise stall the hotkey press that asked for it.
            // Recordings live in the same folder and sort in with the screenshots, so the
            // newest entry after a recording is an .mp4 -- which loaded as "It is not an
            // image WinShot can read" for a command the user invoked as Pin last screenshot.
            path = await Task.Run(() =>
                _history.GetItems().FirstOrDefault(p => !HistoryService.IsVideo(p)));
        }
        catch (Exception ex)
        {
            Log.Error("Could not read history for Pin last screenshot", ex);
            ShowBalloon("Pin", FriendlyError.For(ex), ToastSeverity.Error);
            return;
        }

        if (path is null)
        {
            ShowBalloon("Pin", "No capture in history to pin.", ToastSeverity.Info, holdLong: true);
            return;
        }

        if (await LoadBitmapCopyAsync(path) is not SD.Bitmap bmp)
            return;

        ShowPin(bmp);
    }

    private void PinFromOverlay(FastQuickActionsWindow overlay)
    {
        ShowPin(overlay.CloneImage());
        overlay.Close();
    }

    /// <summary>
    /// Every pin App raises is created here. The pencil chip itself is wired once at startup
    /// on <see cref="FastPinWindow.DefaultEditHandler"/>, so a pin built anywhere else -- the
    /// recording completion card -- gets a working Edit too.
    /// </summary>
    private FastPinWindow ShowPin(SD.Bitmap image, string perfLabel = "pin window")
    {
        var win = new FastPinWindow(image, _settings);
        PerfLog.TrackFirstShown(win, perfLabel);
        win.Show();
        return win;
    }

    /// <summary>The pin's pencil chip: the handoff Annotate already performs from the card,
    /// with the pin left on screen and the editor given its own copy.</summary>
    private void OpenEditorFromPin(SD.Bitmap image)
    {
        var win = EditorWindow.CreateForCapture(image, _settings, _history);
        TrackFirstRender(win, "editor window");
        win.Show();
    }

    private async void OcrFromOverlay(FastQuickActionsWindow overlay)
    {
        try
        {
            using var image = overlay.CloneImage();
            // The card is right there and holds the image the text came from, so it says so
            // itself instead of raising a toast somewhere else on screen.
            await RunOcrToClipboard(image, card: overlay);
        }
        catch (Exception ex)
        {
            Log.Error("OCR from overlay failed", ex);
            ShowBalloon("OCR failed", FriendlyError.For(ex), ToastSeverity.Error);
        }
    }

    private void BackgroundFromOverlay(FastQuickActionsWindow overlay)
    {
        var win = new BackgroundComposerWindow(overlay.CloneImage(), _settings, _history);
        TrackFirstRender(win, "background window");
        win.Show();
        overlay.Close();
    }

    private async void EditFromHistory(string path)
    {
        // Videos route to the video editor. Branching here rather than at the call sites so
        // History, the tray, and the winshot://edit: command all reach it the same way — the
        // completion card used to be the only door to this window.
        if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var editor = new VideoEditorWindow(path, _settings, _history);
            TrackFirstRender(editor, "video editor window");
            editor.Show();
            return;
        }

        if (await LoadBitmapCopyAsync(path) is SD.Bitmap bmp)
        {
            var win = EditorWindow.CreateForCapture(bmp, _settings, _history);
            TrackFirstRender(win, "editor window");
            win.Show();
        }
    }

    private async void PinFromHistory(string path)
    {
        if (await LoadBitmapCopyAsync(path) is SD.Bitmap bmp)
            ShowPin(bmp);
    }

    private async void RestoreFromHistory(string path)
    {
        if (await LoadBitmapCopyAsync(path) is SD.Bitmap bmp)
            ShowOverlay(bmp, path);
    }

    private async void PinMultipleFromHistory(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (await LoadBitmapCopyAsync(path) is not SD.Bitmap bmp)
                continue;

            ShowPin(bmp, "history pin window");
        }
    }

    /// <summary>Loads an image as a detached copy so the file on disk stays unlocked.</summary>
    private async Task<SD.Bitmap?> LoadBitmapCopyAsync(string path)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var fromDisk = new SD.Bitmap(path);
                return new SD.Bitmap(fromDisk);
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load image {path}", ex);
            ShowBalloon("Couldn't open that file", "It is not an image WinShot can read.", ToastSeverity.Error);
            return null;
        }
    }

    private static double SystemDpiScale
    {
        get { try { return GetDpiForSystem() / 96.0; } catch { return 1.0; } }
    }

    /// <summary>
    /// The app's one feedback channel. Everything except "update available" goes to the
    /// in-app toast at the tray corner: the shell balloon renders "Save failed" and
    /// "Copied to clipboard" identically, and Focus Assist swallows it whole with no window
    /// to fall back to. Only one toast at a time — <see cref="ToastWindow"/> enforces that
    /// itself, for every caller, not just this one.
    /// </summary>
    internal void ShowBalloon(
        string title, string message, ToastSeverity severity = ToastSeverity.Info, bool holdLong = false) =>
        ShowToast(title, message, anchor: null, onOpen: null, severity, holdLong);

    /// <summary>
    /// The one way to put a toast on screen, for callers outside App as well: it is what
    /// carries the dispose-on-throw guard and the tray-balloon fallback, so a window that
    /// fails to create still tells the user something instead of leaking a form and going
    /// quiet. Silently does nothing when there is no running App (tests).
    /// </summary>
    internal static void Toast(
        string title, string message, SD.Point? anchor = null, Action? onOpen = null,
        ToastSeverity severity = ToastSeverity.Info, bool holdLong = false)
    {
        if (Current is App app)
            app.ShowToast(title, message, anchor, onOpen, severity, holdLong);
    }

    /// <summary>Returns the toast that went up, or null when the balloon fallback ran. A
    /// caller that put up a working state needs the handle to take it down again when its
    /// result is not itself a toast.</summary>
    private ToastWindow? ShowToast(
        string title, string message, SD.Point? anchor, Action? onOpen, ToastSeverity severity,
        bool holdLong = false, ToastExtras? extras = null)
    {
        try
        {
            var shown = new ToastWindow(title, message, anchor, onOpen, severity, holdLong, extras);
            try
            {
                // Showing is what replaces whatever toast was already up.
                shown.Show();
            }
            catch
            {
                // A form that never made it on screen is not the live toast, and nothing else
                // will ever dispose it — the fallback below is what the user sees instead.
                shown.Dispose();
                throw;
            }
            return shown;
        }
        catch (Exception ex)
        {
            // The toast is a window and a window can fail to create; the balloon is the one
            // piece of feedback that never needs one.
            Log.Error("Toast failed; falling back to the tray balloon", ex);
            ShowShellBalloon(title, message, severity);
            return null;
        }
    }

    /// <summary>The Windows tray balloon, kept for the one message that is clickable from
    /// the shell itself: an available update, which installs on click.</summary>
    private void ShowShellBalloon(string title, string message, ToastSeverity severity = ToastSeverity.Info) =>
        _tray?.ShowBalloonTip(3000, title, message, severity switch
        {
            // The fallback runs on the one path where a window failed to create, which is
            // exactly when the user most needs to know a save failed rather than succeeded.
            ToastSeverity.Error => WF.ToolTipIcon.Error,
            ToastSeverity.Success => WF.ToolTipIcon.Info,
            _ => WF.ToolTipIcon.Info,
        });

    // ---- Updates ----------------------------------------------------------

    /// <summary>Silent startup poll: only surfaces UI when a newer release is found.</summary>
    private async Task StartupUpdateCheckAsync()
    {
        var result = await UpdateService.CheckAsync();
        if (result.State == UpdateState.UpdateAvailable)
            await Dispatcher.InvokeAsync(() => OnUpdateAvailable(result));
    }

    /// <summary>Manual "Check for updates…" — reports the outcome either way.</summary>
    private async Task CheckForUpdatesFlowAsync()
    {
        // A GitHub round trip routinely outlives the 1.7 s a plain Info toast dwells, so the
        // working state used to vanish mid-check and leave the user on a silent screen
        // wondering whether the menu item registered.
        ShowBalloon("Checking for updates…", "Asking GitHub for the latest release.", holdLong: true);
        var result = await UpdateService.CheckAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            switch (result.State)
            {
                case UpdateState.UpdateAvailable:
                    OnUpdateAvailable(result);
                    break;
                case UpdateState.UpToDate:
                    ShowBalloon("Up to date", "You're on the latest version.", ToastSeverity.Success);
                    break;
                default:
                    Log.Error($"Update check failed: {result.Message}");
                    // UpdateService already turned the cause into a friendly line ("GitHub
                    // returned 403.", "No Setup.exe asset on the latest release."). Asserting a
                    // connectivity problem sent the user to fix a network that was fine.
                    ShowBalloon(
                        "Update check failed",
                        string.IsNullOrWhiteSpace(result.Message)
                            ? "WinShot couldn't reach GitHub. Try again in a moment."
                            : result.Message,
                        ToastSeverity.Error);
                    break;
            }
        });
    }

    private void OnUpdateAvailable(UpdateCheckResult result)
    {
        _pendingUpdate = result;
        if (_updateMenuItem is not null)
        {
            _updateMenuItem.Text = $"Install update ({result.LatestVersion})";
            _updateMenuItem.Visible = true;
        }
        ShowShellBalloon("Update available", $"WinShot {result.LatestVersion} is ready — click to install.");
    }

    private async Task InstallPendingUpdateAsync()
    {
        var update = _pendingUpdate;
        if (update?.DownloadUrl is null || _updateInProgress) return;
        _updateInProgress = true;
        try
        {
            // Downloading an installer outlives a check for one, so it holds at least as
            // long: without this the working state vanished and the user watched a silent
            // screen until the app exited and the installer took over.
            ShowBalloon("Downloading update…", "WinShot restarts once it is installed.", holdLong: true);
            await UpdateService.DownloadAndLaunchAsync(update.DownloadUrl, update.LatestVersion ?? "latest");
            // Reached only when the installer launched (failures throw). Exit so it can replace our
            // files; the installer relaunches WinShot itself.
            await Dispatcher.InvokeAsync(Shutdown);
        }
        catch (UpdateVerificationException ex)
        {
            Log.Error("Update verification failed", ex);
            _updateInProgress = false;
            await Dispatcher.InvokeAsync(() => ShowBalloon("Update failed", "The download did not verify. Nothing was installed.", ToastSeverity.Error));
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            _updateInProgress = false;
            await Dispatcher.InvokeAsync(() => ShowBalloon("Update failed", "The installer could not start. The details are in the log.", ToastSeverity.Error));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}

internal sealed class DirectEditCaptureTestHooks
{
    public Action<EditorWindow>? ConfigureEditor { get; init; }
    public Action<EditorWindow>? EditorShown { get; init; }
    public Func<SD.Bitmap, Task>? HistoryWork { get; init; }
    public Func<SD.Bitmap, Task>? AutoCopyWork { get; init; }
}
