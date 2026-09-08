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
    /// <summary>Process that owned the foreground window when the current capture was asked
    /// for. Stamped into the history file name so the tile can show a source-app badge.</summary>
    private string? _captureSourceApp;

    /// <summary>
    /// The <c>?then=</c> set from the URL that started the capture now running, consumed by
    /// the first <see cref="HandleCapture"/> that follows. A single slot is safe because a
    /// single capture is: <see cref="QueueCaptureCommand"/> and <see cref="RunCaptureFlow"/>
    /// both drop anything that arrives while <see cref="_captureInProgress"/> is set.
    /// </summary>
    private PostCaptureSet? _pendingThenOverride;
    private bool _pendingCaptureDrainActive;

    /// <summary>One-shot, run when the current capture flow ends: the History window that hid
    /// itself for the shot puts itself back.</summary>
    private Action? _afterCaptureFlow;
    private long _autoCopySuppressedUntilTick;
    internal DirectEditCaptureTestHooks? DirectEditTestHooks { get; set; }
    internal SettingsService SettingsForTest => _settings;

    private WF.ToolStripMenuItem? _updateMenuItem;
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? incomingArg = e.Args.Length > 0 ? e.Args[0] : null;
        string? incomingCommand = incomingArg is null ? null : CommandServer.ParseCommand(incomingArg);
        CaptureCommandOptions incomingOptions = incomingArg is null
            ? CaptureCommandOptions.None
            : CommandServer.ParseOptions(incomingArg);

        _mutex = new Mutex(true, "WinShot-SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // The mutex says another instance may exist — but verify one is actually serving
            // the command pipe before bowing out. A crashed instance can leave the named mutex
            // behind; without this probe WinShot would refuse to launch (no tray icon, no way
            // to recover short of a reboot).
            bool handledByRunningInstance = incomingCommand is not null
                ? CommandServer.TrySendToRunningInstance(CommandServer.WithQuery(incomingCommand, incomingArg))
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
        // whoever presses the Extract text hotkey first.
        OcrService.Warm();

        AnnounceCompletedUpdate();

        // After the tray icon exists (it is what the bubble points at) and after any startup
        // command has had its turn, so onboarding never lands on top of a capture.
        if (!_settings.Current.HasCompletedFirstRun && incomingCommand is null)
        {
            Dispatcher.BeginInvoke(ShowFirstRunWelcome, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        if (_settings.Current.CheckForUpdatesOnStartup)
            _ = StartupUpdateCheckAsync();

        if (incomingCommand is not null)
            RunCommand(incomingCommand, incomingOptions);

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
            // "Hide the tray icon" is a real preference; the registered hotkeys and
            // winshot://settings stay as the way back in.
            Visible = _settings.Current.ShowTrayIcon,
            Text = "WinShot",
        };

        var s = _settings.Current;
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            // A fixed left gutter for the capture verbs. The housekeeping group at the
            // bottom deliberately gets no glyphs, which demotes it without another separator.
            ShowImageMargin = true,
        };
        // Non-clickable header showing the running version — always visible at a glance.
        var versionHeader = new WF.ToolStripMenuItem(AppInfo.VersionLabel) { Enabled = false };
        menu.Items.Add(versionHeader);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Capture region", s.HotkeyCaptureRegion, () => QueueCaptureCommand("capture-area"), ""));
        menu.Items.Add(MenuItem("Capture window", s.HotkeyCaptureWindow, () => QueueCaptureCommand("capture-window"), ""));
        menu.Items.Add(MenuItem("Capture window with background", null, () => QueueCaptureCommand("capture-window-background"), ""));
        menu.Items.Add(MenuItem("Capture fullscreen", s.HotkeyCaptureFullscreen, () => QueueCaptureCommand("capture-fullscreen"), ""));
        menu.Items.Add(MenuItem("Capture fullscreen (self-timer)", s.HotkeySelfTimer, () => QueueCaptureCommand("self-timer"), ""));
        menu.Items.Add(MenuItem("Capture region (self-timer)", null, () => QueueCaptureCommand("self-timer-region"), ""));
        menu.Items.Add(MenuItem("Capture display…", null, () => QueueCaptureCommand("capture-display"), ""));
        menu.Items.Add(MenuItem("Open clipboard image", s.HotkeyLoadClipboard, LoadClipboardImageFlow, ""));
        menu.Items.Add(MenuItem("Capture previous region", s.HotkeyCapturePrevious, () => QueueCaptureCommand("capture-previous"), ""));
        menu.Items.Add(MenuItem("All-in-one…", s.HotkeyAllInOne, () => QueueCaptureCommand("all-in-one"), ""));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Record screen", s.HotkeyRecord, RecordFlow, "\uE714"));
        menu.Items.Add(MenuItem("Record display…", null, RecordDisplayFlow, "\uE7F4"));
        menu.Items.Add(MenuItem("Capture text (OCR)", s.HotkeyOcr, () => QueueCaptureCommand("ocr"), "\uE8D2"));
        menu.Items.Add(MenuItem("Scrolling capture", s.HotkeyScrolling, () => QueueCaptureCommand("scrolling"), "\uE8CB"));
        menu.Items.Add(MenuItem("Horizontal scrolling capture", null, () => QueueCaptureCommand("scroll-horizontal"), "\uE8AB"));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Open image…", null, OpenImageFileFlow, "\uE8E5"));
        menu.Items.Add(MenuItem("Pin to the screen…", null, PinFromFileFlow, "\uE718"));
        menu.Items.Add(MenuItem("Restore recently closed", null, RestoreLastFlow, "\uE7A7"));
        _desktopIconsMenuItem = MenuItem("Hide desktop icons", null, ToggleDesktopIcons, "\uE8B7");
        menu.Items.Add(_desktopIconsMenuItem);
        _recentsMenuItem = new WF.ToolStripMenuItem("Recent captures");
        _recentsMenuItem.DropDownOpening += (_, _) => PopulateRecentCaptures();
        menu.Items.Add(_recentsMenuItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("History…", s.HotkeyHistory, OpenHistory));
        menu.Items.Add(MenuItem("Settings…", null, OpenSettings));
        menu.Items.Add(MenuItem("Unlock pinned windows", null, FastPinWindow.UnlockAllPins));
        menu.Items.Add(new WF.ToolStripSeparator());
        // Dynamic "Update available" item — hidden until a check finds a newer release.
        _updateMenuItem = MenuItem("Install update", null, () => _ = InstallPendingUpdateAsync());
        _updateMenuItem.Visible = false;
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(MenuItem("Check for updates…", null, () => _ = CheckForUpdatesFlowAsync()));
        menu.Items.Add(MenuItem("About WinShot…", null, OpenAbout));
        menu.Items.Add(MenuItem("Exit", null, Shutdown));
        // The desktop-icon row's checked state is only true while WinShot is holding them
        // hidden, and a capture guard can change that behind the menu's back.
        menu.Opening += (_, _) => RefreshDesktopIconsMenuItem();
        // Re-rasterized on every open, not once: the tray menu follows the pointer, and a
        // 100 % raster opening on the 150 % monitor is the whole defect.
        ScaleMenuGlyphs(menu);
        menu.Opening += (_, _) => ScaleMenuGlyphs(menu);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => QueueCaptureCommand("capture-area");
        _tray.BalloonTipClicked += (_, _) => { if (_pendingUpdate is not null) _ = InstallPendingUpdateAsync(); };
        _settings.Changed += ApplyTrayVisibility;
    }

    private IDisposable? _trayConfirmation;

    /// <summary>
    /// A short confirmation beat on the tray mark for the two silent successes — a copy and
    /// a save. Both currently finish with a toast that can be missed and nothing else; the
    /// icon is the one thing already on screen and already looked at.
    /// </summary>
    private void PlayTrayConfirmation()
    {
        if (_tray is null || !_tray.Visible)
            return;

        _trayConfirmation?.Dispose();
        _trayConfirmation = Motion.Run(
            TrayConfirmationMs,
            // Linear, because the callback below is a three-phase TIMELINE and needs the
            // clock, not a shape. EaseOutBack driving it directly put the eased value above
            // 1.0 from t=0.36 to t=0.85, which the clamp then flattened: the badge collapsed
            // at a third of the way in and the rest of the tween drew nothing while still
            // holding the high-resolution timer.
            static p => p,
            progress =>
            {
                double scale = TrayIconFactory.ConfirmationBadgeScale(progress);
                if (_tray is null)
                    return;
                // TrayIconFactory quantizes the badge to seven cached steps, so ~78 frames
                // produce at most 7 icons. Assigning every frame anyway cost a
                // Shell_NotifyIcon round-trip per frame for no visible difference.
                SD.Icon icon = TrayIconFactory.Create(TrayIconVariant.Confirm, scale);
                if (!ReferenceEquals(_tray.Icon, icon))
                    _tray.Icon = icon;
            },
            () =>
            {
                _trayConfirmation = null;
                RefreshDesktopIconsMenuItem(); // restores Normal, or Slashed if that is the state
            });
    }

    private const int TrayConfirmationMs = TrayIconFactory.ConfirmationMs;

    private WF.ToolStripMenuItem? _desktopIconsMenuItem;
    private WF.ToolStripMenuItem? _recentsMenuItem;
    private bool _desktopIconsHiddenByTray;

    private void ApplyTrayVisibility()
    {
        if (_tray is null) return;
        bool wanted = _settings.Current.ShowTrayIcon;
        if (_tray.Visible != wanted)
            _tray.Visible = wanted;
    }

    /// <summary>The manual toggle over the same DesktopIcons the capture guard uses. The
    /// tray mark picks up a slash while WinShot is the one holding them hidden, because a
    /// hidden desktop with no visible cause is a support call.</summary>
    private void ToggleDesktopIcons()
    {
        try
        {
            if (DesktopIcons.Visible)
            {
                DesktopIcons.Hide();
                _desktopIconsHiddenByTray = true;
            }
            else
            {
                DesktopIcons.Show();
                _desktopIconsHiddenByTray = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Toggling desktop icons failed", ex);
            ShowBalloon("Desktop icons", "Windows would not let WinShot change the desktop right now.", ToastSeverity.Error);
        }
        RefreshDesktopIconsMenuItem();
    }

    private void RefreshDesktopIconsMenuItem()
    {
        if (_desktopIconsMenuItem is null) return;
        bool hidden = !DesktopIcons.Visible;
        _desktopIconsMenuItem.Checked = hidden;
        _desktopIconsMenuItem.Text = hidden ? "Show desktop icons" : "Hide desktop icons";
        if (!hidden)
            _desktopIconsHiddenByTray = false;
        if (_tray is not null)
        {
            _tray.Icon = _desktopIconsHiddenByTray
                ? TrayIconFactory.Create(TrayIconVariant.Slashed)
                : TrayIconFactory.Create();
        }
    }

    /// <summary>The last few captures, with their own thumbnails in the icon gutter that is
    /// now there anyway.</summary>
    private void PopulateRecentCaptures()
    {
        if (_recentsMenuItem is null) return;
        foreach (WF.ToolStripItem item in _recentsMenuItem.DropDownItems)
            item.Image?.Dispose();
        _recentsMenuItem.DropDownItems.Clear();

        List<string> recent;
        try
        {
            recent = _history.GetItems().Take(RecentCaptureCount).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Reading recent captures for the tray menu failed", ex);
            recent = [];
        }

        if (recent.Count == 0)
        {
            _recentsMenuItem.DropDownItems.Add(new WF.ToolStripMenuItem("No captures yet") { Enabled = false });
            return;
        }

        foreach (string path in recent)
        {
            string file = path;
            var item = new WF.ToolStripMenuItem(Path.GetFileName(file))
            {
                // Same gutter as the Segoe glyphs above it, so it has to be the same size at
                // the same DPI — a fixed 16 px thumbnail sits small in a 24 px gutter at 150%.
                Image = TryLoadThumbnail(file, MenuGlyphPixels(_recentsMenuItem.Owner?.DeviceDpi ?? 96)),
            };
            item.Click += (_, _) => EditFromHistory(file);
            _recentsMenuItem.DropDownItems.Add(item);
        }
    }

    private const int RecentCaptureCount = 6;

    private static SD.Image? TryLoadThumbnail(string path, int size)
    {
        try
        {
            if (!File.Exists(path) || Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                return null;
            using var source = new SD.Bitmap(path);
            return source.GetThumbnailImage(size, size, null, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error($"Thumbnail for the tray menu failed ({Path.GetFileName(path)})", ex);
            return null;
        }
    }

    /// <summary>Open an image from disk straight into the editor.</summary>
    private void OpenImageFileFlow()
    {
        string? path = AskForImageFile("Open an image");
        if (path is not null)
            EditFromHistory(path);
    }

    /// <summary>Pin an image from disk to the screen — FastPinWindow already takes a bitmap.</summary>
    private async void PinFromFileFlow()
    {
        string? path = AskForImageFile("Pin an image to the screen");
        if (path is null)
            return;
        if (await LoadBitmapCopyAsync(path) is not SD.Bitmap bmp)
            return;
        try
        {
            var win = new FastPinWindow(bmp, _settings);
            PerfLog.TrackFirstShown(win, "pin window");
            win.Show();
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            Log.Error("Pinning an opened image failed", ex);
            ShowBalloon("Pin to the screen", FriendlyError.For(ex), ToastSeverity.Error);
        }
    }

    private static string? AskForImageFile(string title)
    {
        try
        {
            using var dialog = new WF.OpenFileDialog
            {
                Title = title,
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
                CheckFileExists = true,
            };
            return dialog.ShowDialog() == WF.DialogResult.OK ? dialog.FileName : null;
        }
        catch (Exception ex)
        {
            Log.Error("Opening the file picker failed", ex);
            return null;
        }
    }

    /// <param name="glyph">A Segoe Fluent codepoint for the capture verbs. Housekeeping rows
    /// pass none on purpose: an empty gutter is what visually demotes them.</param>
    private static WF.ToolStripMenuItem MenuItem(
        string text, string? shortcut, Action onClick, string? glyph = null)
    {
        var item = new WF.ToolStripMenuItem(text)
        {
            ShortcutKeyDisplayString = shortcut,
            // The glyph rides on the item so ScaleMenuGlyphs can re-render it at whatever
            // DPI the menu is opening on; the raster itself is made there, not here.
            Tag = glyph,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Rasterizes the menu's glyphs for the DPI the strip is on right now. WinForms scales
    /// the row's font and height for us and does nothing for a bitmap, so a fixed 16 px
    /// raster sat next to text that had grown to 24 px on Isaiah's 150 % primary — the
    /// AutoScaleMode.Dpi landmine on a new surface. Both the bitmap and the point size come
    /// off <see cref="WF.Control.DeviceDpi"/>, and ImageScalingSize is set to match so the
    /// strip does not bilinearly resample what we just drew at the right size.
    /// </summary>
    private static void ScaleMenuGlyphs(WF.ContextMenuStrip menu)
    {
        int dpi = menu.DeviceDpi <= 0 ? 96 : menu.DeviceDpi;
        int size = MenuGlyphPixels(dpi);
        menu.ImageScalingSize = new SD.Size(size, size);
        foreach (WF.ToolStripItem item in menu.Items)
        {
            if (item.Tag is string glyph && glyph.Length > 0)
                item.Image = RenderMenuGlyph(glyph, dpi);
        }
    }

    /// <summary>16 DIP in device pixels. The tray menu is a WinForms surface, so nothing
    /// scales a bitmap for us — 16 device px sat beside text WinForms had already grown to
    /// ~24 on a 150 % monitor.</summary>
    internal static int MenuGlyphPixels(int dpi) => Math.Max(16, (int)Math.Round(16 * dpi / 96.0));

    private static readonly Dictionary<(string Glyph, int Dpi), SD.Image> MenuGlyphCache = new();

    /// <summary>A monoline glyph for the menu's left gutter, 16 DIP at <paramref name="dpi"/>,
    /// cached for the process per size — a cache keyed by glyph alone hands a 100 % raster to
    /// a 150 % monitor the moment the menu opens on one.</summary>
    private static SD.Image? RenderMenuGlyph(string glyph, int dpi)
    {
        if (MenuGlyphCache.TryGetValue((glyph, dpi), out SD.Image? cached))
            return cached;
        try
        {
            int size = MenuGlyphPixels(dpi);
            var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.Clear(SD.Color.Transparent);
                // AntiAliasGridFit, not ClearType: subpixel AA needs an opaque ground to
                // blend against, and this bitmap is transparent — the glyph edges would come
                // back colour-fringed with partial alpha. It is also the only text rendering
                // hint the rest of the app uses.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                // A point size renders at the Graphics' DPI, and a bitmap's is always 96 —
                // so the glyph has to be told to grow, exactly like the box around it.
                using var font = ThemePalette.IconFont(ThemePalette.TextBodyPt * dpi / 96f);
                using var brush = new SD.SolidBrush(ThemePalette.TextPrimary);
                using var format = new SD.StringFormat
                {
                    Alignment = SD.StringAlignment.Center,
                    LineAlignment = SD.StringAlignment.Center,
                };
                g.DrawString(glyph, font, brush, new SD.RectangleF(0, 0, size, size), format);
            }
            MenuGlyphCache[(glyph, dpi)] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Error("Tray menu glyph could not be rendered", ex);
            return null;
        }
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
        Reg(s.HotkeyHistory, OpenHistory);
        Reg(s.HotkeySelfTimer, () => QueueCaptureCommand("self-timer"));
        Reg(s.HotkeyLoadClipboard, LoadClipboardImageFlow);

        // The rest of the tray's commands, bound through the ShortcutBindings dictionary
        // rather than a Settings field each. Unbound actions are absent from it, so this
        // registers only what the user actually asked for.
        foreach ((string id, Action handler) in BindableCommands())
        {
            if (s.ShortcutBindings.TryGetValue(id, out string? gesture))
                Reg(gesture, handler);
        }

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

    /// <summary>
    /// Every command that can be bound but has no dedicated Hotkey* field. The ids match
    /// SettingsTruthCatalog.ExecutableShortcutIds, which the truth-catalog test pins, so a
    /// row can never appear in Settings without something here to run it.
    /// </summary>
    private IEnumerable<(string Id, Action Handler)> BindableCommands()
    {
        yield return ("capture-window-background", () => QueueCaptureCommand("capture-window-background"));
        yield return ("capture-display", () => QueueCaptureCommand("capture-display"));
        yield return ("record-display", RecordDisplayFlow);
        yield return ("scroll-horizontal", () => QueueCaptureCommand("scroll-horizontal"));
        yield return ("open-image", OpenImageFileFlow);
        yield return ("pin-to-screen", PinFromFileFlow);
        yield return ("restore-last", RestoreLastFlow);
        yield return ("unlock-pins", FastPinWindow.UnlockAllPins);
        yield return ("toggle-desktop-icons", ToggleDesktopIcons);
        yield return ("settings", OpenSettings);
        yield return ("about", OpenAbout);
    }

    /// <summary>Test seam: the ids this app can actually dispatch.</summary>
    internal IEnumerable<string> BindableCommandIdsForTest() => BindableCommands().Select(c => c.Id);

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
        if (cmd is null)
            return;
        CaptureCommandOptions options = CommandServer.ParseOptions(raw);
        Dispatcher.BeginInvoke(() => RunCommand(cmd, options));
    }

    private void RunCommand(string cmd, CaptureCommandOptions? options = null)
    {
        options ??= CaptureCommandOptions.None;

        if (IsCaptureCommand(cmd))
        {
            if (options.DelaySeconds > 0)
                RunDelayedCaptureCommand(cmd, options);
            else
                QueueCaptureCommand(cmd, options.Then);
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
            case "load-clipboard": LoadClipboardImageFlow(); break;
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
        "all-in-one" or "ocr" or "scrolling" or "scroll-horizontal" or "self-timer" or "self-timer-region"
        || cmd.StartsWith("scroll-region:", StringComparison.OrdinalIgnoreCase);

    private bool _delayedCountdownActive;

    /// <summary>
    /// Counts the delay down on screen rather than sleeping: <c>?delayed=5</c> means the user
    /// is arranging something, and a countdown they can watch is the difference between a
    /// timer and an app that has hung. The ring is click-through and never activates, so it
    /// cannot be cancelled — Escape goes to whatever is underneath it, which is the point of
    /// a countdown you can keep working during. Runs outside the capture guard so the command
    /// still queues normally when the count reaches zero, and takes a guard of its own so two
    /// links cannot stack two rings — the second capture would be dropped by the capture
    /// guard anyway, leaving the user watching a countdown that produced nothing.
    /// </summary>
    private async void RunDelayedCaptureCommand(string cmd, CaptureCommandOptions options)
    {
        if (_captureInProgress || _delayedCountdownActive || Recording.SelectionUiActive) return;
        _delayedCountdownActive = true;
        try
        {
            await FastSelfTimerWindow.RunAsync(options.DelaySeconds);
        }
        catch (Exception ex)
        {
            Log.Error("Delayed capture countdown failed", ex);
        }
        finally
        {
            _delayedCountdownActive = false;
        }
        QueueCaptureCommand(cmd, options.Then);
    }

    /// <summary>Queues a capture command, or returns false when one was already running and this
    /// press is dropped — callers that moved a window out of the way need to know which.</summary>
    private bool QueueCaptureCommand(string cmd, PostCaptureSet? thenOverride = null)
    {
        // A capture flow is already active (selector open, overlay finishing): drop the
        // command instead of parking it in the pending slot. Every caller is user-initiated
        // (hotkey, tray, winshot://), and replaying a press made while the selector was
        // already open reads as a ghost re-trigger right after the user cancels with Esc.
        if (_captureInProgress || Recording.SelectionUiActive) return false;
        // Read the owning app now, while the user's window is still foreground — once the
        // selector overlay is up, or the history write runs on the thread pool, the answer
        // is WinShot. History tags the file with it so the card can wear an app badge.
        _captureSourceApp = FileNamer.GetForegroundInfo().App;
        _pendingCaptureCommand = cmd;
        _pendingThenOverride = thenOverride;
        SchedulePendingCaptureDrain();
        return true;
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
            case "scroll-horizontal": ScrollingFlow(ScrollCaptureCommand.DirectionForCommand(cmd)); break;
            case "self-timer": CaptureFullscreenTimerFlow(); break;
            case "self-timer-region": CaptureRegionFlow(delaySeconds: _settings.Current.SelfTimerSeconds); break;
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

    /// <summary>Takes the URL's <c>?then=</c> set once; null for every capture that did not
    /// come from a URL carrying one.</summary>
    private PostCaptureSet? TakeThenOverride()
    {
        PostCaptureSet? then = _pendingThenOverride;
        _pendingThenOverride = null;
        return then;
    }

    private void FinishCaptureFlow()
    {
        _captureInProgress = false;
        // A cancelled flow never reached HandleCapture; drop its override with it.
        _pendingThenOverride = null;
        MemoryCleanup.Request();
        ResumeAfterCaptureFlow();
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
        if (_captureInProgress || Recording.SelectionUiActive)
        {
            // FinishCaptureFlow is the only thing that runs the pending restore, and this return
            // never reaches it. A flow that IS in progress will run it on its own way out; with
            // nothing in progress, nothing ever would — History would stay hidden with no way
            // back but the hotkey.
            if (!_captureInProgress) ResumeAfterCaptureFlow();
            return;
        }
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

    /// <summary>Whoever stepped out of the frame for a capture comes back now — whether the user
    /// took one, pressed Escape, or the command was dropped before a flow ever started.</summary>
    private void ResumeAfterCaptureFlow()
    {
        Action? after = _afterCaptureFlow;
        _afterCaptureFlow = null;
        after?.Invoke();
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
                    _pendingThenOverride = null;
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
        FastRegionSelectorDialog.SelectorMode mode = FastRegionSelectorDialog.SelectorMode.Area,
        int delaySeconds = 0) => RunCaptureFlow(
        "Region capture failed", "Capture failed", async () =>
        {
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            PerfLog.TrackFirstShown(selector, mode == FastRegionSelectorDialog.SelectorMode.Window ? "capture-window selector" : "capture-area selector");
            try
            {
                if (await selector.ShowAsync(mode, elementHover: mode == FastRegionSelectorDialog.SelectorMode.Area) == WF.DialogResult.OK &&
                    selector.SelectedRegionPx is SD.Rectangle region)
                {
                    // A delayed capture must photograph what the screen looks like AFTER the
                    // countdown, so the frozen crop taken at confirm time is exactly wrong for it.
                    SD.Bitmap? frozenCrop = SelfTimerOptions.ShouldRunDelay(delaySeconds)
                        ? null
                        : selector.TakeCapturedRegion();
                    if (frozenCrop is null)
                    {
                        await WaitForOverlayDismissAsync();
                        if (SelfTimerOptions.ShouldRunDelay(delaySeconds))
                            await FastSelfTimerWindow.RunAsync(delaySeconds);
                        frozenCrop = await CaptureSelectedRegionAsync(region, "capture-area");
                    }
                    // selector.Mode, not the mode we ASKED for: Space flips Area<->Window
                    // mid-session, so a capture-window run that ended as a hand-drawn
                    // rectangle used to come back floating on the wallpaper with 48 px of
                    // window padding and a mockup shadow — and with the wallpaper backdrop
                    // the default, that fired with no opt-in at all.
                    if (selector.Mode == FastRegionSelectorDialog.SelectorMode.Window &&
                        postCaptureActionOverride is null &&
                        !selector.BareBackgroundRequested &&
                        WindowBackdrop.IsEnabled(_settings.Current.WindowCaptureBackground))
                    {
                        // Off the dispatcher, like every other capture step here: decoding
                        // the wallpaper and compositing the shadow cost ~200 ms, and running
                        // them inline froze the UI thread between the overlay closing and the
                        // card appearing — which reads as capture latency and stalls any
                        // motion in flight.
                        SD.Bitmap bare = frozenCrop;
                        (SD.Bitmap image, bool composed) result =
                            await RunCaptureWorkAsync(() => ComposeWindowBackdrop(bare));
                        frozenCrop = result.image;
                        if (!result.composed)
                        {
                            // The user asked for a composite and got something else, so this
                            // is the degraded-outcome glyph, not a neutral notice. The
                            // capture itself is not lost, which is what the copy says.
                            ShowBalloon("Window capture",
                                "Saved the bare window — the backdrop could not be drawn.",
                                ToastSeverity.Error);
                        }
                    }
                    HandleCapture(frozenCrop, postCaptureActionOverride);
                }
            }
            finally
            {
                FastRegionSelectorDialog.Return(selector);
            }
        });

    /// <summary>
    /// Puts a captured window on its backdrop, off the UI thread. Failure here must never
    /// lose the capture, so a broken wallpaper or an out-of-memory canvas returns the bare
    /// shot with <c>composed: false</c>, which the caller turns into one info line — the user
    /// asked for a composite and would otherwise get a different image with no explanation.
    /// </summary>
    private (SD.Bitmap Image, bool Composed) ComposeWindowBackdrop(SD.Bitmap window)
    {
        try
        {
            SD.Bitmap composed = WindowBackdrop.ComposeOnWallpaper(
                window, WindowBackdrop.PaddingFor(_settings.Current.WindowCapturePadding));
            window.Dispose();
            return (composed, true);
        }
        catch (Exception ex)
        {
            Log.Error("Window backdrop could not be composed; saving the bare window", ex);
            return (window, false);
        }
    }

    /// <summary>
    /// Opens whatever image is on the clipboard in the editor. Editing a picture someone
    /// pasted to you used to mean saving it to a file first — nothing in the app read an
    /// image out of the clipboard at all.
    /// </summary>
    private void LoadClipboardImageFlow() => RunCaptureFlow(
        "Open clipboard image failed", "Could not open the clipboard image", async () =>
        {
            SD.Bitmap? image = await RunCaptureWorkAsync(ClipboardImage.TryRead);
            if (image is null)
            {
                ShowBalloon("Open clipboard image", "There is no image on the clipboard.",
                    ToastSeverity.Info, holdLong: true);
                return;
            }
            HandleCapture(image, PostCaptureAction.Edit);
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

            // Timer waits too: the whole point is to photograph what the screen looks like
            // AFTER the delay, so the frozen crop is deliberately not used and the overlay
            // has to be gone before the countdown starts.
            if (action is AllInOneAction.Scroll or AllInOneAction.Timer ||
                (captured is null && action is AllInOneAction.Capture or AllInOneAction.Ocr))
            {
                await WaitForOverlayDismissAsync();
            }

            if (captured is null && action is AllInOneAction.Capture or AllInOneAction.Ocr)
                captured = await CaptureSelectedRegionAsync(regionPx, $"all-in-one {action.ToString().ToLowerInvariant()}");

            switch (action)
            {
                case AllInOneAction.Capture:
                    HandleCapture(captured!);
                    break;
                case AllInOneAction.Timer:
                    await FastSelfTimerWindow.RunAsync(_settings.Current.SelfTimerSeconds);
                    HandleCapture(await CaptureSelectedRegionAsync(regionPx, "all-in-one timer"));
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
        WithCursorIfWanted(
            DesktopIconCaptureGuard.Run(
                _settings.Current.HideDesktopIconsDuringCapture,
                () => DesktopIcons.Visible,
                DesktopIcons.Hide,
                DesktopIcons.Show,
                Thread.Sleep,
                CaptureService.CaptureVirtualDesktop),
            CaptureService.VirtualScreen);

    private SD.Bitmap CaptureScreenRegionRespectingSettings(SD.Rectangle screenRect) =>
        WithCursorIfWanted(
            DesktopIconCaptureGuard.Run(
                _settings.Current.HideDesktopIconsDuringCapture,
                () => DesktopIcons.Visible,
                DesktopIcons.Hide,
                DesktopIcons.Show,
                Thread.Sleep,
                () => CaptureService.CaptureScreenRegion(screenRect)),
            screenRect);

    /// <summary>
    /// Draws the pointer into a live grab when the user asked for it. Only live grabs: the
    /// selector's frozen snapshot excludes its own overlay and never has a cursor in it, so
    /// there is nothing to honour on that path.
    /// </summary>
    private SD.Bitmap WithCursorIfWanted(SD.Bitmap capture, SD.Rectangle screenRect)
    {
        if (_settings.Current.CursorOnScreenshots)
            CursorOverlay.Draw(capture, screenRect);
        return capture;
    }

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

    /// <summary>Every captured bitmap funnels through here: optional downscale, then the
    /// after-capture set (history included).</summary>
    private void HandleCapture(
        SD.Bitmap bmp,
        string? postCaptureActionOverride = null,
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular,
        PostCaptureSet? actionsOverride = null)
    {
        // A winshot://…?then=copy,save URL overrides the after-capture set for this capture
        // only. Taken (and cleared) unconditionally, so an override that never reached a
        // capture — a cancelled selector — cannot leak into the next hotkey press.
        actionsOverride ??= TakeThenOverride();

        // The shutter, at the moment the pixels exist. Fire-and-forget: audio never
        // delays the capture or the surface that follows it.
        CaptureSound.Play(_settings.Current);

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

        // The standing "frame every shot like this" preference, applied after the downscale
        // so the padding is measured against the pixels that ship. None (the default) does
        // nothing, and a backdrop that fails to compose returns null rather than a capture.
        //
        // AppliesTo, not IsEnabled: the "Capture window with background" override is on its
        // way to the interactive composer, which frames the shot itself.
        if (ScreenshotBackdrop.AppliesTo(_settings.Current.ScreenshotBackground, postCaptureActionOverride) &&
            ScreenshotBackdrop.Compose(
                bmp,
                _settings.Current.ScreenshotBackground,
                _settings.Current.ScreenshotBackgroundColor,
                _settings.Current.ScreenshotBackgroundShadow) is SD.Bitmap framed)
        {
            bmp.Dispose();
            bmp = framed;
        }

        // "Add background" has no matrix cell — it only ever arrives as a per-invocation
        // override (the tray's "Capture window with background").
        if (postCaptureActionOverride == PostCaptureAction.Background)
        {
            _ = AddHistoryAsync(bmp, cloneOnCallerThread: true, historyKind);
            try
            {
                var composer = new BackgroundComposerWindow(bmp, _settings, _history);
                TrackFirstRender(composer, "background window");
                composer.Show();
            }
            catch { bmp.Dispose(); throw; }
            return;
        }

        PostCaptureSet actions = actionsOverride
            ?? (postCaptureActionOverride is not null
                ? PostCaptureSet.Single(postCaptureActionOverride, alsoCopy: _settings.Current.ScreenshotCopy)
                : PostCaptureSet.ForScreenshot(_settings.Current));

        // Every box unticked would mean a capture that vanishes with no trace on screen.
        // Show the card instead — history still has it, and the user can see what happened.
        if (actions.IsEmpty)
            actions = actions with { Overlay = true };

        RunPostCaptureSet(bmp, actions, historyKind);
    }

    /// <summary>
    /// Runs the after-capture <b>set</b>. Order is fixed: history, then the disk write, then
    /// the windows that stay open, then the clipboard, and the Quick Access card last so it
    /// lands on top. Each consumer holds its own clone, so the clipboard write landing after
    /// two windows opened costs nothing.
    /// </summary>
    /// <remarks>
    /// One consumer owns the original bitmap and the deferred history + auto-copy work that
    /// hangs off it — the Quick Access card when it is on, otherwise the annotation editor.
    /// Every other consumer gets its own clone taken on THIS thread: a background clone races
    /// the new owner's LockBits/Dispose on a single-accessor GDI+ bitmap, and a set can hand
    /// the same capture to four owners at once.
    /// </remarks>
    private void RunPostCaptureSet(SD.Bitmap bmp, PostCaptureSet actions, HistoryCaptureKind historyKind)
    {
        bool cardOwns = actions.Overlay;
        bool editorOwns = !cardOwns && actions.Annotate;
        bool ownerRunsHistoryAndCopy = cardOwns || editorOwns;

        try
        {
            if (!ownerRunsHistoryAndCopy)
                _ = AddHistoryAsync(bmp, cloneOnCallerThread: true, historyKind);

            // PostCaptureSet's field order IS this order, and its remarks say why: the sinks
            // that finish and let go of their copy first, then the windows that stay open,
            // then the card on top. Copy leads because it is the one the user can lose — with
            // "Ask for a name" on, Save opens a modal, and a clipboard write queued behind
            // that modal does not happen until the user answers it.
            if (actions.Copy && !ownerRunsHistoryAndCopy)
            {
                _ = CopyToClipboardAndNotifyAsync(
                    CaptureService.CloneBitmap(bmp),
                    takeOwnership: true,
                    showSuccess: true,
                    showFailure: true,
                    includePng: true,
                    failureContext: "Copy failed");
            }

            if (actions.Save)
                SaveSilently(CaptureService.CloneBitmap(bmp)); // takes ownership

            if (actions.Annotate && !editorOwns)
                OpenDirectEditCapture(CaptureService.CloneBitmap(bmp), historyKind, addHistory: false, autoCopy: false);

            if (actions.Pin)
            {
                // The clone is the pin's the moment the constructor takes it; the outer catch
                // only knows about the original, so a pin window that fails to CONSTRUCT must
                // free it. Past the constructor the window owns it — freeing it there would
                // leave a live window painting from a disposed bitmap.
                SD.Bitmap forPin = CaptureService.CloneBitmap(bmp);
                FastPinWindow pin;
                try { pin = new FastPinWindow(forPin, _settings); }
                catch { forPin.Dispose(); throw; }
                PerfLog.TrackFirstShown(pin, "pin window");
                pin.Show();
            }

            if (cardOwns)
            {
                ShowOverlayWithDeferredCaptureWork(bmp, autoCopy: actions.Copy, historyKind);
                return;
            }

            if (editorOwns)
            {
                OpenDirectEditCapture(bmp, historyKind, addHistory: true, autoCopy: actions.Copy);
                return;
            }
        }
        catch
        {
            bmp.Dispose();
            throw;
        }

        // No owner took the original (copy/save/pin already hold their own clones).
        bmp.Dispose();
    }

    /// <param name="addHistory">False when another consumer in the same after-capture set
    /// already owns the single history add.</param>
    private void OpenDirectEditCapture(
        SD.Bitmap bmp,
        HistoryCaptureKind historyKind,
        bool addHistory = true,
        bool autoCopy = true)
    {
        DirectEditCaptureTestHooks? hooks = DirectEditTestHooks;
        // Read the tag NOW, on the caller's thread. The field is overwritten by the next
        // QueueCaptureCommand and this write runs later on the thread pool, so a second hotkey
        // press straight after a capture used to stamp capture B's "@app" onto capture A's file.
        string? sourceApp = _captureSourceApp;
        Func<SD.Bitmap, Task> historyWork = addHistory
            ? hooks?.HistoryWork ?? (source =>
                Task.Run(() =>
                {
                    try { _history.Add(source, historyKind, sourceApp); }
                    catch (Exception ex) { Log.Error("Failed to add direct Edit capture to history", ex); }
                }))
            : _ => Task.CompletedTask;
        Func<SD.Bitmap, Task>? autoCopyWork = autoCopy
            ? hooks?.AutoCopyWork ?? CopyBorrowedCaptureToClipboardAsync
            : null;
        var handoff = new DirectEditCaptureHandoff(bmp, historyWork, autoCopyWork);

        // This method owns the bitmap it is handed, so the guard lives here: it is the only
        // one that also covers the CLONE the "annotate alongside the card" branch passes,
        // which RunPostCaptureSet's own catch knows nothing about.
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
        HistoryCaptureKind historyKind = HistoryCaptureKind.Regular,
        PostCaptureSet? actionsOverride = null) =>
        HandleCapture(bmp, postCaptureActionOverride, historyKind, actionsOverride);

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

        string? sourceApp = _captureSourceApp;
        return Task.Run(() =>
        {
            try
            {
                using var copy = CaptureService.CloneBitmap(bmp);
                return _history.Add(copy, historyKind, sourceApp);
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
        string? sourceApp = _captureSourceApp;
        return Task.Run(() =>
        {
            using (copy)
            {
                try { return _history.Add(copy, historyKind, sourceApp); }
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
                await Dispatcher.InvokeAsync(() =>
                {
                    NotifyAction(
                        NotifiedAction.Copy, "Copied", "The capture is on the clipboard.",
                        trayBeat: true);
                });
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
            string? sourceApp = _captureSourceApp;
            string? historyPath = await Task.Run(() =>
            {
                try
                {
                    using var copy = CaptureService.CloneBitmap(bmp);
                    return _history.Add(copy, historyKind, sourceApp);
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
            path = FileNamer.NextUniquePath(
                _settings,
                _settings.Current.SaveFolder,
                ImageFormatChooser.Resolve(_settings.Current.ImageFormat, bmp));
        }
        catch (Exception ex)
        {
            bmp.Dispose();
            Log.Error("Could not choose a save path", ex);
            ShowBalloon("Save failed", FriendlyError.For(ex), ToastSeverity.Error);
            return;
        }

        if (_settings.Current.AskForNameAfterCapture)
        {
            // The resolved template is the default, so the prompt costs one Enter for
            // anyone who only wanted the option available.
            string suggested = Path.GetFileNameWithoutExtension(path);
            string? answer;
            try
            {
                answer = ThemedInputDialog.Ask(
                    null,
                    "Name this capture",
                    $"Saving into {_settings.Current.SaveFolder}",
                    suggested);
            }
            catch (Exception ex)
            {
                // This runs inside the after-capture set. Letting a dialog failure escape kills
                // every action queued behind it — the pin and the card never open — and the user
                // gets one generic "Capture failed". Fall back to the resolved name instead.
                Log.Error("The name prompt could not be shown; saving under the default name", ex);
                answer = suggested;
            }
            if (answer is null)
            {
                // Cancelling a name prompt is a decision, not a failure — but it is still a
                // decision the user has to see landing, or the app just goes quiet on them.
                bmp.Dispose();
                Log.Info("Save cancelled at the name prompt");
                NotifyAction(NotifiedAction.Save, "Save cancelled",
                    "The capture is still in History.", severity: ToastSeverity.Info);
                return;
            }
            string chosen = ThemedInputDialog.SanitizeFileName(answer, suggested);
            if (!string.Equals(chosen, suggested, StringComparison.Ordinal))
            {
                try
                {
                    path = FileNamer.UniquePathFor(
                        _settings.Current.SaveFolder, chosen, Path.GetExtension(path));
                }
                catch (Exception ex)
                {
                    bmp.Dispose();
                    Log.Error("Could not use the name you entered", ex);
                    ShowBalloon("Save failed", FriendlyError.For(ex), ToastSeverity.Error);
                    return;
                }
            }
        }

        _ = Task.Run(() =>
        {
            using (bmp)
            {
                try
                {
                    ImageSaver.Save(bmp, path);
                    Dispatcher.InvokeAsync(() =>
                    {
                        NotifyAction(
                            NotifiedAction.Save, "Saved", Path.GetFileName(path), trayBeat: true);
                    });
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
        try
        {
            var sw = Stopwatch.StartNew();
            // OcrUnavailableException is the one exception in this flow whose message is
            // written for the user, so it is the one allowed on screen verbatim. Catching
            // InvalidOperationException instead caught the type and not the message: any
            // framework InvalidOperationException from the SoftwareBitmap/GDI+ work inside
            // ExtractAsync reached the user as raw exception text.
            result = await OcrService.ExtractAsync(bmp, _settings.Current.OcrJoinLines);
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
                    card.FlashStatus("No text found");
                else
                    // The one OCR result the user has to read and act on, and the only one
                    // with no action attached - so it holds the screen as long as a success.
                    ShowOcrToast(
                        "Nothing found", "No text or codes in the selection.", anchor,
                        onOpen: null, ToastSeverity.Info, holdLong: true);
                return;
            }

            await CaptureService.SetTextToClipboardAsync(payload.ClipboardText);
            if (card is not null)
                card.FlashStatus("Text copied");
            else
                ShowOcrToast(payload.BalloonTitle, payload.Preview, anchor, BuildOpenAction(payload.ClipboardText));
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

    /// <summary>Severity is per-call: "Nothing found" is not a success, and a checkmark on it
    /// reads as "it worked".</summary>
    private void ShowOcrToast(
        string title, string preview, SD.Point? anchor, Action? onOpen,
        ToastSeverity severity = ToastSeverity.Success, bool holdLong = false) =>
        NotifyAction(NotifiedAction.Ocr, title, preview, anchor, onOpen, severity, holdLong);

    private void ScrollingFlow(ScrollDirection? direction = null) => RunCaptureFlow(
        "Scrolling capture failed", "Scrolling capture failed", async () =>
        {
            SD.Rectangle? picked = null;
            bool paneClick = false;
            var selector = FastRegionSelectorDialog.Rent(CreateWindowListTask, _settings);
            PerfLog.TrackFirstShown(selector, "scrolling selector");
            try
            {
                if (await selector.ShowAsync(paneHover: true) == WF.DialogResult.OK &&
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
            var (stitched, cancelled) = await ScrollingStatusWindow.Run(refined, direction, paneScoped: paneClick);
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
        win.NewCaptureRequested -= NewCaptureFromHistory;
        win.NewCaptureRequested += NewCaptureFromHistory;
    }

    /// <summary>
    /// The empty grid's "New capture" button. The region selector freezes the screen as it is and
    /// History is a 1200x760 window in the middle of it, so History has to be out of the frame or
    /// the primary call to action captures the window it lives in. The grid is empty by definition
    /// while that button is reachable, so there is nothing on screen worth keeping.
    /// </summary>
    private async void NewCaptureFromHistory(HistoryWindow win)
    {
        win.HideForCapture();
        // Let the compositor actually take the window off screen before the selector freezes it.
        await WaitForOverlayDismissAsync();
        if (QueueCaptureCommand("capture-area"))
            _afterCaptureFlow = win.ShowAfterCapture;
        else
            win.ShowAfterCapture();
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

    private void PinFromOverlay(FastQuickActionsWindow overlay)
    {
        var win = new FastPinWindow(overlay.CloneImage(), _settings);
        PerfLog.TrackFirstShown(win, "pin window");
        win.Show();
        // The card is dismissed by a pin, exactly as it always has been — that dismissal is
        // part of the pinned surface and not this track's to change. The outline-to-solid
        // swap therefore has to be instant rather than a 140 ms cross-fade: it lands before
        // the card's own exit begins, so the glyph reads solid for the length of the exit
        // instead of racing it.
        overlay.Close();
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
        {
            var win = new FastPinWindow(bmp, _settings);
            PerfLog.TrackFirstShown(win, "pin window");
            win.Show();
        }
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

            var win = new FastPinWindow(bmp, _settings);
            PerfLog.TrackFirstShown(win, "history pin window");
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
    /// One message whose body keeps changing — a download percentage — not twenty messages.
    /// Re-showing a toast builds a new window and disposes the old one outright, so a
    /// progress readout replayed the 280 ms entrance twenty times and announced itself to a
    /// screen reader on each one. Updating the live card in place is what progress means.
    /// </summary>
    private void ShowProgressToast(string title, string message)
    {
        if (!ToastWindow.TryUpdateBody(title, message))
            ShowBalloon(title, message, holdLong: true);
    }

    /// <summary>
    /// A routine success — text recognised, capture copied, capture saved — confirmed in
    /// whichever channel the user chose for that action. Off means off, tray beat included:
    /// someone who silenced "Copied" is not asking for a quieter "Copied".
    /// </summary>
    /// <remarks>
    /// Only successes come through here. <see cref="ShowBalloon"/> stays the path for
    /// failures, which are never silenced by a preference about confirmations.
    /// </remarks>
    private void NotifyAction(
        NotifiedAction action,
        string title,
        string message,
        SD.Point? anchor = null,
        Action? onOpen = null,
        ToastSeverity severity = ToastSeverity.Success,
        bool holdLong = false,
        bool trayBeat = false)
    {
        switch (NotificationStyle.For(action, _settings.Current))
        {
            case NotificationStyle.Off:
                return;
            // A shell balloon needs a visible NotifyIcon to come out of. With "Show icon"
            // off the NotifyIcon is alive but invisible and ShowBalloonTip is a silent
            // no-op, so the user picks a confirmation channel and gets nothing. Fall back
            // to the in-app toast, which needs no tray at all.
            case NotificationStyle.System when _tray is { Visible: true }:
                // The shell notification carries no buttons, so an action (the OCR "Open
                // link") is lost here by construction. The text is already on the clipboard.
                ShowShellBalloon(title, message, severity);
                break;
            default:
                ShowToast(title, message, anchor, onOpen, severity, holdLong);
                break;
        }

        if (trayBeat)
            PlayTrayConfirmation();
    }

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

    private void ShowToast(
        string title, string message, SD.Point? anchor, Action? onOpen, ToastSeverity severity,
        bool holdLong = false)
    {
        try
        {
            var shown = new ToastWindow(title, message, anchor, onOpen, severity, holdLong);
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
        }
        catch (Exception ex)
        {
            // The toast is a window and a window can fail to create; the balloon is the one
            // piece of feedback that never needs one — as long as the tray icon is showing.
            // When it is not there is nowhere left to put this, and the log is the record.
            Log.Error("Toast failed; falling back to the tray balloon", ex);
            ShowShellBalloon(title, message, severity);
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

    private FirstRunWelcomeWindow? _welcome;

    /// <summary>
    /// The first launch, once. Three rows that RUN the commands rather than describing them,
    /// so the user's first capture happens inside onboarding — which is the only part of a
    /// tour anyone remembers.
    /// </summary>
    private void ShowFirstRunWelcome()
    {
        if (_welcome is not null || _settings.Current.HasCompletedFirstRun)
            return;

        // Written before the window opens: if anything below throws, or the process dies
        // mid-onboarding, the user is not taught the same lesson again on every launch.
        _settings.Current.HasCompletedFirstRun = true;
        try { _settings.Save(); } catch (Exception ex) { Log.Error("Failed to record first run", ex); }

        try
        {
            var s = _settings.Current;
            var actions = new List<FirstRunWelcomeWindow.WelcomeAction>
            {
                new("\uE7C5", "Capture a region", HintFor(s.HotkeyCaptureRegion), () => QueueCaptureCommand("capture-area")),
                new("\uE714", "Record the screen", HintFor(s.HotkeyRecord), RecordFlow),
                new("\uE8D2", "Extract text", HintFor(s.HotkeyOcr), () => QueueCaptureCommand("ocr")),
            };

            _welcome = new FirstRunWelcomeWindow(TrayAnchorPoint(), actions, () => _welcome = null);
            _welcome.Show();
        }
        catch (Exception ex)
        {
            Log.Error("First-run welcome could not be shown", ex);
            _welcome = null;
        }
    }

    private static string HintFor(string? gesture) =>
        string.IsNullOrWhiteSpace(gesture) ? "No shortcut set" : gesture;

    /// <summary>
    /// A point on the notification area, for the welcome bubble's tail. The tray icon has no
    /// public rectangle, so this uses the notification area's own window — and falls back to
    /// the taskbar corner, which is where it is even when Shell_TrayWnd cannot be found.
    /// </summary>
    private static SD.Point? TrayAnchorPoint()
    {
        try
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && GetWindowRect(notify, out NativeRect r))
                    return new SD.Point((r.Left + r.Right) / 2, r.Top);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not locate the notification area for the welcome bubble", ex);
        }

        SD.Rectangle area = WF.Screen.PrimaryScreen?.WorkingArea ?? SD.Rectangle.Empty;
        return area.IsEmpty ? null : new SD.Point(area.Right - 60, area.Bottom);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    /// <summary>
    /// The installer swaps WinShot.exe under the running process and relaunches it, so the
    /// only thing the user saw was the app vanishing and coming back. Comparing the stored
    /// version to this build's is what turns that into "Updated to 1.4.0".
    /// </summary>
    private void AnnounceCompletedUpdate()
    {
        string current = AppInfo.Version;
        string previous = _settings.Current.LastRunVersion;
        if (string.Equals(previous, current, StringComparison.Ordinal))
            return;

        _settings.Current.LastRunVersion = current;
        try { _settings.Save(); } catch (Exception ex) { Log.Error("Failed to record the running version", ex); }

        // A first run has nothing to compare against, and announcing "updated" to someone who
        // just installed WinShot would be a lie.
        if (string.IsNullOrEmpty(previous))
            return;

        ShowBalloon($"Updated to {current}", $"You were on {previous}. See what changed in Settings, About.", ToastSeverity.Success, holdLong: true);
    }

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
            int lastShownPercent = 0;
            await UpdateService.DownloadAndLaunchAsync(
                update.DownloadUrl,
                update.LatestVersion ?? "latest",
                onProgress: fraction =>
                {
                    if (fraction is not double value) return;
                    int percent = (int)Math.Round(value * 100);
                    // Re-showing the toast replaces the one on screen; every byte would
                    // rebuild a window, so only move in visible steps.
                    if (percent < lastShownPercent + 5 && percent < 100) return;
                    lastShownPercent = percent;
                    Dispatcher.InvokeAsync(() => ShowProgressToast(
                        "Downloading update…",
                        $"{percent}% of WinShot {update.LatestVersion}. It restarts once installed."));
                });
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
