using System.Windows;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Editor;
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
    private Mutex? _mutex;
    private bool _ownsMutex;
    private WF.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private readonly SettingsService _settings = new();
    private HistoryService _history = null!;
    private RecordingController? _recording;
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
        _recording = new RecordingController(_settings, _history);
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

    // ---- Feature entry points ----

    private void RecordFlow() => _recording?.ToggleFlow();

    private async void OcrFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            SD.Bitmap? crop = null;
            using (var shot = CaptureService.CaptureVirtualDesktop())
            {
                var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows());
                if (selector.ShowDialog() == true && selector.SelectedRegionPx is SD.Rectangle region)
                    crop = CaptureService.Crop(shot, region);
            }
            if (crop is null) return;
            using (crop)
                await RunOcrToClipboard(crop);
        }
        catch (Exception ex)
        {
            Log.Error("OCR capture failed", ex);
            ShowBalloon("OCR failed", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private async Task RunOcrToClipboard(SD.Bitmap bmp)
    {
        try
        {
            string text = await OcrService.ExtractTextAsync(bmp);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowBalloon("OCR", "No text found in the selection.");
                return;
            }
            Clipboard.SetText(text);
            string preview = text.Length > 80 ? text[..80] + "…" : text;
            ShowBalloon("Text copied to clipboard", preview);
        }
        catch (InvalidOperationException ex)
        {
            ShowBalloon("OCR unavailable", ex.Message);
        }
    }

    private async void ScrollingFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            SD.Rectangle? picked = null;
            using (var shot = CaptureService.CaptureVirtualDesktop())
            {
                var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows());
                if (selector.ShowDialog() == true && selector.SelectedRegionPx is SD.Rectangle r)
                    picked = r;
            }
            if (picked is not SD.Rectangle region) return;

            region.Offset(CaptureService.VirtualScreen.X, CaptureService.VirtualScreen.Y);
            var stitched = await ScrollingStatusWindow.Run(region);
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
            _captureInProgress = false;
        }
    }

    private void OpenHistory()
    {
        var win = HistoryWindow.Show(_history, _settings);
        win.EditRequested -= EditFromHistory;
        win.EditRequested += EditFromHistory;
        win.PinRequested -= PinFromHistory;
        win.PinRequested += PinFromHistory;
    }

    private void OpenSettings() => SettingsWindow.Show(_settings);

    private void OpenEditorFromOverlay(QuickActionsWindow overlay)
    {
        new EditorWindow(overlay.CloneImage(), _settings, _history).Show();
        overlay.Close();
    }

    private void PinFromOverlay(QuickActionsWindow overlay)
    {
        new PinWindow(overlay.CloneImage()).Show();
        overlay.Close();
    }

    private async void OcrFromOverlay(QuickActionsWindow overlay)
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

    private void EditFromHistory(string path)
    {
        if (LoadBitmapCopy(path) is SD.Bitmap bmp)
            new EditorWindow(bmp, _settings, _history).Show();
    }

    private void PinFromHistory(string path)
    {
        if (LoadBitmapCopy(path) is SD.Bitmap bmp)
            new PinWindow(bmp).Show();
    }

    /// <summary>Loads an image as a detached copy so the file on disk stays unlocked.</summary>
    private SD.Bitmap? LoadBitmapCopy(string path)
    {
        try
        {
            using var fromDisk = new SD.Bitmap(path);
            return new SD.Bitmap(fromDisk);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load image {path}", ex);
            ShowBalloon("WinShot", "Could not open that file as an image.");
            return null;
        }
    }

    internal void ShowBalloon(string title, string message) =>
        _tray?.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Info);
}
