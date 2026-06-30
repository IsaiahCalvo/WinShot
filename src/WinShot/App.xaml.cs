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
    private bool _captureInProgress;
    private string? _pendingCaptureCommand;
    private bool _pendingCaptureDrainActive;
    private long _autoCopySuppressedUntilTick;

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
                    MessageBox.Show("WinShot is already running — look for it in the system tray.", "WinShot");
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
            ShowBalloon("WinShot error", args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

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
        _settings.Changed += RegisterHotkeys;

        _ = Task.Run(ProtocolRegistrar.EnsureRegistered);
        // Self-heal the launch-at-startup entry against the current exe path, so a moved or
        // updated WinShot.exe still launches at login (Settings only writes it on save).
        _ = Task.Run(() => StartupRegistration.Reconcile(_settings.Current.LaunchAtStartup));
        _commandServer = new CommandServer();
        _commandServer.CommandReceived += OnCommandReceived;
        _commandServer.Start();

        SetupTray();
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        ScheduleIdleWarmups();
        Log.Info("WinShot started");

        if (_settings.Current.CheckForUpdatesOnStartup)
            _ = StartupUpdateCheckAsync();

        if (incomingCommand is not null)
            RunCommand(incomingCommand);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Persist in-memory settings (e.g. FileNamer's counter) without re-triggering hotkey registration.
        _settings.Changed -= RegisterHotkeys;
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
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(MenuItem("Capture area", s.HotkeyCaptureRegion, () => QueueCaptureCommand("capture-area")));
        menu.Items.Add(MenuItem("Capture window", s.HotkeyCaptureWindow, () => QueueCaptureCommand("capture-window")));
        menu.Items.Add(MenuItem("Capture window with background", null, () => QueueCaptureCommand("capture-window-background")));
        menu.Items.Add(MenuItem("Capture fullscreen", s.HotkeyCaptureFullscreen, () => QueueCaptureCommand("capture-fullscreen")));
        menu.Items.Add(MenuItem("Capture fullscreen (self-timer)", null, () => QueueCaptureCommand("self-timer")));
        menu.Items.Add(MenuItem("Capture display…", null, () => QueueCaptureCommand("capture-display")));
        menu.Items.Add(MenuItem("Capture previous area", s.HotkeyCapturePrevious, () => QueueCaptureCommand("capture-previous")));
        menu.Items.Add(MenuItem("All-in-One…", s.HotkeyAllInOne, () => QueueCaptureCommand("all-in-one")));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Record screen", s.HotkeyRecord, RecordFlow));
        menu.Items.Add(MenuItem("Record display…", null, RecordDisplayFlow));
        menu.Items.Add(MenuItem("Capture text (OCR)", s.HotkeyOcr, () => QueueCaptureCommand("ocr")));
        menu.Items.Add(MenuItem("Scrolling capture", s.HotkeyScrolling, () => QueueCaptureCommand("scrolling")));
        menu.Items.Add(MenuItem("Horizontal scrolling capture", null, () => QueueCaptureCommand("scroll-horizontal")));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("History…", null, OpenHistory));
        menu.Items.Add(MenuItem("Settings…", null, OpenSettings));
        menu.Items.Add(MenuItem("Unlock pinned windows", null, FastPinWindow.UnlockAllPins));
        menu.Items.Add(new WF.ToolStripSeparator());
        // Dynamic "Update available" item — hidden until a check finds a newer release.
        _updateMenuItem = MenuItem("Install update", null, () => _ = InstallPendingUpdateAsync());
        _updateMenuItem.Visible = false;
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(MenuItem("Check for updates…", null, () => _ = CheckForUpdatesFlowAsync()));
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

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var s = _settings.Current;
        var failed = new List<string>();
        if (!_hotkeys.Register(s.HotkeyCaptureRegion, () => QueueCaptureCommand("capture-area"))) failed.Add(s.HotkeyCaptureRegion);
        if (!_hotkeys.Register(s.HotkeyCaptureWindow, () => QueueCaptureCommand("capture-window"))) failed.Add(s.HotkeyCaptureWindow);
        if (!_hotkeys.Register(s.HotkeyCaptureFullscreen, () => QueueCaptureCommand("capture-fullscreen"))) failed.Add(s.HotkeyCaptureFullscreen);
        if (!_hotkeys.Register(s.HotkeyRecord, RecordFlow)) failed.Add(s.HotkeyRecord);
        if (!_hotkeys.Register(s.HotkeyOcr, () => QueueCaptureCommand("ocr"))) failed.Add(s.HotkeyOcr);
        if (!_hotkeys.Register(s.HotkeyScrolling, () => QueueCaptureCommand("scrolling"))) failed.Add(s.HotkeyScrolling);
        if (!_hotkeys.Register(s.HotkeyCapturePrevious, () => QueueCaptureCommand("capture-previous"))) failed.Add(s.HotkeyCapturePrevious);
        if (!_hotkeys.Register(s.HotkeyAllInOne, () => QueueCaptureCommand("all-in-one"))) failed.Add(s.HotkeyAllInOne);
        if (failed.Count > 0)
            ShowBalloon("Some hotkeys unavailable", string.Join(", ", failed));
    }

    private void ScheduleIdleWarmups()
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                foreach (var stage in StartupWarmupPlan.LightweightStartupStages())
                    ScheduleWarmup(stage.DelayMs, stage.Name, () => RunStartupWarmup(stage.Kind));
            }));
    }

    private void RunStartupWarmup(StartupWarmupKind kind)
    {
        switch (kind)
        {
            case StartupWarmupKind.CaptureSelectors:
                FastRegionSelectorDialog.Prewarm();
                FastDisplayPickerDialog.Prewarm();
                FastAllInOneSelectorDialog.Prewarm();
                break;
        }
    }

    private void ScheduleWarmup(int delayMs, string name, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Warmup failed: {name}", ex);
            }
        };
        timer.Start();
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
        "all-in-one" or "ocr" or "scrolling" or "scroll-horizontal" or "self-timer";

    private void QueueCaptureCommand(string cmd)
    {
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
            case "scroll-horizontal": ScrollingFlow(ScrollCaptureCommand.ChoiceForCommand(cmd)); break;
            case "self-timer": CaptureFullscreenTimerFlow(); break;
        }
    }

    private void FinishCaptureFlow()
    {
        _captureInProgress = false;
        MemoryCleanup.Request();
        SchedulePendingCaptureDrain();
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
                    await Task.Delay(50);
                    continue;
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

    private async void CaptureRegionFlow(
        string? postCaptureActionOverride = null,
        FastRegionSelectorDialog.SelectorMode mode = FastRegionSelectorDialog.SelectorMode.Area)
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            TrackFirstShown(selector, mode == FastRegionSelectorDialog.SelectorMode.Window ? "capture-window selector" : "capture-area selector");
            try
            {
                if (await selector.ShowAsync(mode) == WF.DialogResult.OK && selector.SelectedRegionPx is SD.Rectangle region)
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
        }
        catch (Exception ex)
        {
            Log.Error("Region capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void CaptureFullscreenFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var capture = await RunCaptureWorkAsync(CaptureDesktopRespectingSettings);
            LogPerf("capture-fullscreen screen grab", sw);
            HandleCapture(capture);
        }
        catch (Exception ex)
        {
            Log.Error("Fullscreen capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void CaptureFullscreenTimerFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            await FastSelfTimerWindow.RunAsync(_settings.Current.SelfTimerSeconds);
            var sw = Stopwatch.StartNew();
            var capture = await RunCaptureWorkAsync(CaptureDesktopRespectingSettings);
            LogPerf("self-timer screen grab", sw);
            HandleCapture(capture);
        }
        catch (Exception ex)
        {
            Log.Error("Self-timer capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void CaptureDisplayFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            if (FastDisplayPickerDialog.ChooseDisplay() is SD.Rectangle r)
            {
                var sw = Stopwatch.StartNew();
                var capture = await RunCaptureWorkAsync(() => CaptureScreenRegionRespectingSettings(r));
                LogPerf("capture-display screen grab", sw);
                HandleCapture(capture);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Display capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void CapturePreviousFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            if (PreviousRegion.TryParse(_settings.Current.LastCaptureRegion, out SD.Rectangle r))
            {
                var sw = Stopwatch.StartNew();
                var capture = await RunCaptureWorkAsync(() => CaptureScreenRegionRespectingSettings(r));
                LogPerf("capture-previous screen grab", sw);
                HandleCapture(capture);
            }
            else
                ShowBalloon("Capture previous area", "No previous region yet — capture an area first.");
        }
        catch (Exception ex)
        {
            Log.Error("Capture-previous failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void AllInOneFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            AllInOneAction action;
            SD.Rectangle regionPx;
            SD.Bitmap? captured = null;

            var selector = FastAllInOneSelectorDialog.Rent(CreateWindowListTask, _settings);
            try
            {
                TrackFirstShown(selector, "all-in-one selector");
                if (await selector.ShowAsync() != WF.DialogResult.OK || selector.SelectedRegionPx is not SD.Rectangle rp)
                    return;

                action = selector.SelectedAction;
                regionPx = rp;
            }
            finally
            {
                FastAllInOneSelectorDialog.Return(selector);
            }

            if (action is AllInOneAction.Capture or AllInOneAction.Ocr or AllInOneAction.Scroll)
                await WaitForOverlayDismissAsync();

            if (action is AllInOneAction.Capture or AllInOneAction.Ocr)
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
                    Recording.ToggleFlow();
                    break;
                case AllInOneAction.Scroll:
                    var region = regionPx;
                    region.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);
                    var stitched = await ScrollingStatusWindow.Run(region);
                    if (stitched is not null)
                        HandleCapture(stitched);
                    else
                        ShowBalloon("Scrolling capture", "Nothing captured.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("All-in-one capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

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

    private static void TrackFirstShown(WF.Form form, string metricName)
    {
        var sw = Stopwatch.StartNew();
        bool logged = false;
        EventHandler? shownHandler = null;
        EventHandler? visibleHandler = null;

        void LogOnce()
        {
            if (logged) return;
            logged = true;
            if (shownHandler is not null)
                form.Shown -= shownHandler;
            if (visibleHandler is not null)
                form.VisibleChanged -= visibleHandler;
            LogPerf($"{metricName} first show", sw);
        }

        shownHandler = (_, _) => LogOnce();
        visibleHandler = (_, _) =>
        {
            if (form.Visible)
                LogOnce();
        };
        form.Shown += shownHandler;
        form.VisibleChanged += visibleHandler;
        if (form.Visible)
            LogOnce();
    }

    private static void LogPerf(string metricName, Stopwatch sw) =>
        Log.Info($"Perf {metricName}: {sw.ElapsedMilliseconds} ms");

    private static Task<T> RunCaptureWorkAsync<T>(Func<T> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Every captured bitmap funnels through here: optional downscale, history, then the post-capture action.</summary>
    private void HandleCapture(SD.Bitmap bmp, string? postCaptureActionOverride = null)
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
            try { ShowOverlayWithDeferredCaptureWork(bmp, autoCopy: _settings.Current.AutoCopyToClipboard); }
            catch { bmp.Dispose(); throw; }
            return;
        }

        Task<string?> historyPathTask = AddHistoryAsync(
            bmp,
            cloneOnCallerThread: PostCaptureAction.NeedsCallerThreadHistoryClone(action));
        if (_settings.Current.AutoCopyToClipboard && action != PostCaptureAction.Copy)
            QueueAutoClipboardCopy(bmp);

        switch (action)
        {
            case PostCaptureAction.Copy:
                QueueClipboardCopy(bmp, takeOwnership: true, showSuccess: true, showFailure: true, includePng: true, failureContext: "Copy failed");
                break;
            case PostCaptureAction.Save:
                SaveSilently(bmp); // takes ownership
                break;
            case PostCaptureAction.Edit:
                // The window takes ownership and disposes on close — but its ctor
                // touches GDI before wiring that up, so dispose here if it throws.
                try
                {
                    var win = EditorWindow.CreateForCapture(bmp, _settings, _history);
                    TrackFirstRender(win, "editor window");
                    win.Show();
                }
                catch { bmp.Dispose(); throw; }
                break;
            case PostCaptureAction.Pin:
                try
                {
                    var win = new FastPinWindow(bmp, _settings);
                    FastPinWindow.TrackFirstShown(win, "pin window");
                    win.Show();
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

    private Task<string?> AddHistoryAsync(SD.Bitmap bmp, bool cloneOnCallerThread)
    {
        if (cloneOnCallerThread)
        {
            try
            {
                return AddHistoryCopyAsync(CaptureService.CloneBitmap(bmp));
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
                return _history.Add(copy);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to add capture to history", ex);
                return null;
            }
        });
    }

    private Task<string?> AddHistoryCopyAsync(SD.Bitmap copy)
    {
        return Task.Run(() =>
        {
            using (copy)
            {
                try { return _history.Add(copy); }
                catch (Exception ex)
                {
                    Log.Error("Failed to add capture to history", ex);
                    return null;
                }
            }
        });
    }

    private RecordingController Recording => _recording ??= new RecordingController(_settings, _history);

    private void QueueClipboardCopy(SD.Bitmap bmp, bool takeOwnership, bool showSuccess, bool showFailure, bool includePng, string failureContext)
    {
        _ = CopyToClipboardAndNotifyAsync(bmp, takeOwnership, showSuccess, showFailure, includePng, failureContext);
    }

    private void QueueAutoClipboardCopy(SD.Bitmap bmp)
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _autoCopySuppressedUntilTick))
        {
            Log.Info("Auto-copy to clipboard skipped: clipboard temporarily unavailable.");
            return;
        }

        _ = CopyToClipboardAndNotifyAsync(
            bmp,
            takeOwnership: false,
            showSuccess: false,
            showFailure: false,
            includePng: false,
            failureContext: "Auto-copy to clipboard skipped",
            isAutoCopy: true);
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
                await Dispatcher.InvokeAsync(() => ShowBalloon("WinShot", "Copied to clipboard."));
        }
        catch (Exception ex)
        {
            if (showFailure)
            {
                Log.Error(failureContext, ex);
                await Dispatcher.InvokeAsync(() => ShowBalloon("Copy failed", ex.Message));
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

    private void ShowOverlay(SD.Bitmap bmp, Task<string?>? historyPathTask)
    {
        var sw = Stopwatch.StartNew();
        var overlay = new FastQuickActionsWindow(bmp, _settings, historyPathTask: historyPathTask);
        long createMs = sw.ElapsedMilliseconds;
        FastQuickActionsWindow.TrackFirstShown(overlay, "quick actions overlay");
        WireOverlay(overlay);
        long wireMs = sw.ElapsedMilliseconds - createMs;
        overlay.Show();
        long showMs = sw.ElapsedMilliseconds - createMs - wireMs;
        if (sw.ElapsedMilliseconds > 50)
            Log.Info($"Perf quick actions overlay breakdown: create={createMs} wire={wireMs} show={showMs} total={sw.ElapsedMilliseconds} ms");
    }

    private void ShowOverlayWithDeferredCaptureWork(SD.Bitmap bmp, bool autoCopy)
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
        FastQuickActionsWindow.TrackFirstShown(overlay, "quick actions overlay");
        overlay.Shown += (_, _) =>
        {
            if (started) return;
            started = true;
            overlay.BeginInvoke(new Action(() =>
                _ = RunDeferredOverlayCaptureWorkAsync(bmp, historyPathSource, captureWorkComplete, autoCopy)));
        };
        WireOverlay(overlay);
        long wireMs = sw.ElapsedMilliseconds - createMs;
        overlay.Show();
        long showMs = sw.ElapsedMilliseconds - createMs - wireMs;
        if (sw.ElapsedMilliseconds > 50)
            Log.Info($"Perf quick actions overlay breakdown: create={createMs} wire={wireMs} show={showMs} total={sw.ElapsedMilliseconds} ms");
    }

    private async Task RunDeferredOverlayCaptureWorkAsync(
        SD.Bitmap bmp,
        TaskCompletionSource<string?> historyPathCompletion,
        TaskCompletionSource<object?> captureWorkCompletion,
        bool autoCopy)
    {
        try
        {
            string? historyPath = await Task.Run(() =>
            {
                try
                {
                    using var copy = CaptureService.CloneBitmap(bmp);
                    return _history.Add(copy);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to add capture to history", ex);
                    return null;
                }
            }).ConfigureAwait(false);
            historyPathCompletion.TrySetResult(historyPath);

            if (autoCopy)
                QueueAutoClipboardCopy(bmp);

            captureWorkCompletion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            Log.Error("Deferred overlay capture work failed", ex);
            historyPathCompletion.TrySetResult(null);
            captureWorkCompletion.TrySetResult(null);
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
        FastQuickActionsWindow.TrackFirstShown(overlay, "quick actions overlay");
        WireOverlay(overlay);
        long wireMs = sw.ElapsedMilliseconds - createMs;
        overlay.Show();
        long showMs = sw.ElapsedMilliseconds - createMs - wireMs;
        if (sw.ElapsedMilliseconds > 50)
            Log.Info($"Perf quick actions overlay breakdown: create={createMs} wire={wireMs} show={showMs} total={sw.ElapsedMilliseconds} ms");
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
            ShowBalloon("Save failed", ex.Message);
            return;
        }

        _ = Task.Run(() =>
        {
            using (bmp)
            {
                try
                {
                    ImageSaver.Save(bmp, path);
                    Dispatcher.InvokeAsync(() => ShowBalloon("Saved", Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    Log.Error("Silent save failed", ex);
                    Dispatcher.InvokeAsync(() => ShowBalloon("Save failed", ex.Message));
                }
            }
        });
    }

    // ---- Feature entry points ----

    private void RecordFlow() => Recording.ToggleFlow();

    private void RecordDisplayFlow() => Recording.ToggleDisplayFlow();

    private async void OcrFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            SD.Bitmap? crop = null;
            SD.Point? anchor = null;
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            TrackFirstShown(selector, "ocr selector");
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
        }
        catch (Exception ex)
        {
            Log.Error("OCR capture failed", ex);
            ShowBalloon("OCR failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async Task RunOcrToClipboard(SD.Bitmap bmp, SD.Point? anchor = null)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await OcrService.ExtractAsync(bmp, _settings.Current.OcrJoinLines);
            LogPerf("ocr extract", sw);

            OcrClipboardPayload? payload = OcrClipboardFormatter.Build(result);
            if (payload is null)
            {
                ShowOcrToast("Nothing found", "No text or codes in the selection.", anchor, onOpen: null);
                return;
            }

            await CaptureService.SetTextToClipboardAsync(payload.ClipboardText);
            ShowOcrToast(payload.BalloonTitle, payload.Preview, anchor, BuildOpenAction(payload.ClipboardText));
        }
        catch (InvalidOperationException ex)
        {
            ShowBalloon("OCR unavailable", ex.Message);
        }
        catch (COMException ex)
        {
            Log.Info($"OCR clipboard copy skipped: {ex.Message}");
            ShowBalloon("Clipboard unavailable", "Text was found, but Windows clipboard is busy.");
        }
    }

    /// <summary>Returns an Open action when the copied text is a single http(s) URL
    /// (e.g. a decoded QR code), else null.</summary>
    private static Action? BuildOpenAction(string? clipboardText)
    {
        string text = clipboardText?.Trim() ?? string.Empty;
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return () => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        return null;
    }

    private void ShowOcrToast(string title, string preview, SD.Point? anchor, Action? onOpen)
    {
        try
        {
            var toast = new OcrToastWindow(title, preview, anchor, onOpen);
            toast.Show();
        }
        catch (Exception ex)
        {
            Log.Error("OCR toast failed; falling back to balloon", ex);
            ShowBalloon(title, preview);
        }
    }

    private async void ScrollingFlow(ScrollCaptureChoice? choice = null)
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            SD.Rectangle? picked = null;
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            TrackFirstShown(selector, "scrolling selector");
            try
            {
                if (await selector.ShowAsync() == WF.DialogResult.OK && selector.SelectedRegionPx is SD.Rectangle r)
                    picked = r;
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }
            if (picked is not SD.Rectangle region) return;

            await WaitForOverlayDismissAsync();
            region.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);
            var stitched = await ScrollingStatusWindow.Run(region, choice);
            if (stitched is not null)
                HandleCapture(stitched);
            else
                ShowBalloon("Scrolling capture", "Nothing captured.");
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture failed", ex);
            ShowBalloon("Scrolling capture failed", ex.Message);
        }
        finally
        {
            FinishCaptureFlow();
        }
    }

    private async void RestoreLastFlow()
    {
        string? path = FastQuickActionsWindow.PopRecentlyClosed();
        if (path is null)
        {
            ShowBalloon("Restore", "No recently closed capture to restore.");
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
    }

    private void OpenSettings()
    {
        var sw = Stopwatch.StartNew();
        SettingsWindow.Show(_settings);
        LogPerf("settings window show", sw);
    }

    private void OpenEditorFromOverlay(FastQuickActionsWindow overlay)
    {
        var win = EditorWindow.CreateForCapture(overlay.CloneImage(), _settings, _history);
        TrackFirstRender(win, "editor window");
        win.Show();
        overlay.Close();
    }

    private void PinFromOverlay(FastQuickActionsWindow overlay)
    {
        var win = new FastPinWindow(overlay.CloneImage(), _settings);
        FastPinWindow.TrackFirstShown(win, "pin window");
        win.Show();
        overlay.Close();
    }

    private async void OcrFromOverlay(FastQuickActionsWindow overlay)
    {
        try
        {
            using var image = overlay.CloneImage();
            await RunOcrToClipboard(image);
        }
        catch (Exception ex)
        {
            Log.Error("OCR from overlay failed", ex);
            ShowBalloon("OCR failed", ex.Message);
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
        {
            var win = new FastPinWindow(bmp, _settings);
            FastPinWindow.TrackFirstShown(win, "pin window");
            win.Show();
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
            ShowBalloon("WinShot", "Could not open that file as an image.");
            return null;
        }
    }

    private static double SystemDpiScale
    {
        get { try { return GetDpiForSystem() / 96.0; } catch { return 1.0; } }
    }

    internal void ShowBalloon(string title, string message) =>
        _tray?.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Info);

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
        ShowBalloon("WinShot", "Checking for updates…");
        var result = await UpdateService.CheckAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            switch (result.State)
            {
                case UpdateState.UpdateAvailable:
                    OnUpdateAvailable(result);
                    break;
                case UpdateState.UpToDate:
                    ShowBalloon("WinShot", "You're on the latest version.");
                    break;
                default:
                    Log.Error($"Update check failed: {result.Message}");
                    ShowBalloon("WinShot", "Couldn't check for updates.");
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
        ShowBalloon("Update available", $"WinShot {result.LatestVersion} is ready — click to install.");
    }

    private async Task InstallPendingUpdateAsync()
    {
        var update = _pendingUpdate;
        if (update?.DownloadUrl is null || _updateInProgress) return;
        _updateInProgress = true;
        try
        {
            ShowBalloon("WinShot", "Downloading update…");
            await UpdateService.DownloadAndLaunchAsync(update.DownloadUrl, update.LatestVersion ?? "latest");
            // Reached only when the installer launched (failures throw). Exit so it can replace our
            // files; the installer relaunches WinShot itself.
            await Dispatcher.InvokeAsync(Shutdown);
        }
        catch (UpdateVerificationException ex)
        {
            Log.Error("Update verification failed", ex);
            _updateInProgress = false;
            await Dispatcher.InvokeAsync(() => ShowBalloon("WinShot", "Update failed: download verification failed."));
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            _updateInProgress = false;
            await Dispatcher.InvokeAsync(() => ShowBalloon("WinShot", "Update install failed. Log saved."));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
