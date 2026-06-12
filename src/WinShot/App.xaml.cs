using System.Windows;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Overlay;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private WF.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private readonly SettingsService _settings = new();
    private HistoryService _history = null!;
    private bool _captureInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "WinShot-SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("WinShot is already running — look for it in the system tray.", "WinShot");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            ShowBalloon("WinShot error", args.Exception.Message);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _settings.Load();
        _history = new HistoryService(_settings);
        _settings.Changed += RegisterHotkeys;

        SetupTray();
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        Log.Info("WinShot started");
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(MenuItem("Capture region / window", _settings.Current.HotkeyCaptureRegion, CaptureRegionFlow));
        menu.Items.Add(MenuItem("Capture fullscreen", _settings.Current.HotkeyCaptureFullscreen, CaptureFullscreenFlow));
        menu.Items.Add(MenuItem("Record screen", _settings.Current.HotkeyRecord, RecordFlow));
        menu.Items.Add(MenuItem("Capture text (OCR)", _settings.Current.HotkeyOcr, OcrFlow));
        menu.Items.Add(MenuItem("Scrolling capture", _settings.Current.HotkeyScrolling, ScrollingFlow));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("History…", null, OpenHistory));
        menu.Items.Add(MenuItem("Settings…", null, OpenSettings));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Exit", null, Shutdown));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => CaptureRegionFlow();
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
        if (!_hotkeys.Register(s.HotkeyCaptureRegion, CaptureRegionFlow)) failed.Add(s.HotkeyCaptureRegion);
        if (!_hotkeys.Register(s.HotkeyCaptureFullscreen, CaptureFullscreenFlow)) failed.Add(s.HotkeyCaptureFullscreen);
        if (!_hotkeys.Register(s.HotkeyRecord, RecordFlow)) failed.Add(s.HotkeyRecord);
        if (!_hotkeys.Register(s.HotkeyOcr, OcrFlow)) failed.Add(s.HotkeyOcr);
        if (!_hotkeys.Register(s.HotkeyScrolling, ScrollingFlow)) failed.Add(s.HotkeyScrolling);
        if (failed.Count > 0)
            ShowBalloon("Some hotkeys unavailable", string.Join(", ", failed));
    }

    // ---- Capture flows ----

    private void CaptureRegionFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            using var shot = CaptureService.CaptureVirtualDesktop();
            var windows = WindowEnumerator.GetTopLevelWindows();
            var selector = new RegionSelectorWindow(shot, windows);
            if (selector.ShowDialog() == true && selector.SelectedRegionPx is SD.Rectangle region)
                HandleCapture(CaptureService.Crop(shot, region));
        }
        catch (Exception ex)
        {
            Log.Error("Region capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void CaptureFullscreenFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            HandleCapture(CaptureService.CaptureVirtualDesktop());
        }
        catch (Exception ex)
        {
            Log.Error("Fullscreen capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    /// <summary>Every captured bitmap funnels through here: history, clipboard, overlay.</summary>
    private void HandleCapture(SD.Bitmap bmp)
    {
        try { _history.Add(bmp); }
        catch (Exception ex) { Log.Error("Failed to add capture to history", ex); }

        if (_settings.Current.AutoCopyToClipboard)
        {
            try { CaptureService.CopyToClipboard(bmp); }
            catch (Exception ex) { Log.Error("Auto-copy to clipboard failed", ex); }
        }

        var overlay = new QuickActionsWindow(bmp, _settings);
        overlay.EditRequested += OpenEditorFromOverlay;
        overlay.PinRequested += PinFromOverlay;
        overlay.OcrRequested += OcrFromOverlay;
        overlay.Show();
    }

    // ---- Feature entry points (filled in by later phases) ----

    private void RecordFlow() => ShowBalloon("Recording", "Coming soon — not built yet.");

    private void OcrFlow() => ShowBalloon("OCR", "Coming soon — not built yet.");

    private void ScrollingFlow() => ShowBalloon("Scrolling capture", "Coming soon — not built yet.");

    private void OpenHistory() => ShowBalloon("History", "Coming soon — not built yet.");

    private void OpenSettings() => ShowBalloon("Settings", "Coming soon — not built yet.");

    private void OpenEditorFromOverlay(QuickActionsWindow overlay) =>
        ShowBalloon("Editor", "Coming soon — not built yet.");

    private void PinFromOverlay(QuickActionsWindow overlay) =>
        ShowBalloon("Pin", "Coming soon — not built yet.");

    private void OcrFromOverlay(QuickActionsWindow overlay) =>
        ShowBalloon("OCR", "Coming soon — not built yet.");

    internal void ShowBalloon(string title, string message) =>
        _tray?.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Info);
}
