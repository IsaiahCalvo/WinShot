using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    private Mutex? _mutex;
    private bool _ownsMutex;
    private WF.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private CommandServer? _commandServer;
    private readonly SettingsService _settings = new();
    private HistoryService _history = null!;
    private RecordingController? _recording;
    private bool _captureInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? incomingCommand = e.Args.Length > 0 ? CommandServer.ParseCommand(e.Args[0]) : null;

        _mutex = new Mutex(true, "WinShot-SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            // A second launch: forward any command to the running instance, otherwise just say hello.
            if (incomingCommand is not null)
                CommandServer.TrySendToRunningInstance(incomingCommand);
            else
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

        ProtocolRegistrar.EnsureRegistered();
        _commandServer = new CommandServer();
        _commandServer.CommandReceived += OnCommandReceived;
        _commandServer.Start();

        SetupTray();
        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        Log.Info("WinShot started");

        if (incomingCommand is not null)
            RunCommand(incomingCommand);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Persist in-memory settings (e.g. FileNamer's counter) without re-triggering hotkey registration.
        _settings.Changed -= RegisterHotkeys;
        try { _settings.Save(); } catch (Exception ex) { Log.Error("Failed to persist settings on exit", ex); }

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
        menu.Items.Add(MenuItem("Capture region / window", s.HotkeyCaptureRegion, CaptureRegionFlow));
        menu.Items.Add(MenuItem("Capture fullscreen", s.HotkeyCaptureFullscreen, CaptureFullscreenFlow));
        menu.Items.Add(MenuItem("Capture fullscreen (self-timer)", null, CaptureFullscreenTimerFlow));
        menu.Items.Add(MenuItem("Capture display…", null, CaptureDisplayFlow));
        menu.Items.Add(MenuItem("Capture previous area", s.HotkeyCapturePrevious, CapturePreviousFlow));
        menu.Items.Add(MenuItem("All-in-One…", s.HotkeyAllInOne, AllInOneFlow));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("Record screen", s.HotkeyRecord, RecordFlow));
        menu.Items.Add(MenuItem("Capture text (OCR)", s.HotkeyOcr, OcrFlow));
        menu.Items.Add(MenuItem("Scrolling capture", s.HotkeyScrolling, ScrollingFlow));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(MenuItem("History…", null, OpenHistory));
        menu.Items.Add(MenuItem("Settings…", null, OpenSettings));
        menu.Items.Add(MenuItem("Unlock pinned windows", null, PinWindow.UnlockAllPins));
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
        if (!_hotkeys.Register(s.HotkeyCapturePrevious, CapturePreviousFlow)) failed.Add(s.HotkeyCapturePrevious);
        if (!_hotkeys.Register(s.HotkeyAllInOne, AllInOneFlow)) failed.Add(s.HotkeyAllInOne);
        if (failed.Count > 0)
            ShowBalloon("Some hotkeys unavailable", string.Join(", ", failed));
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
        switch (cmd)
        {
            case "capture-area": CaptureRegionFlow(); break;
            case "capture-fullscreen": CaptureFullscreenFlow(); break;
            case "capture-previous": CapturePreviousFlow(); break;
            case "all-in-one": AllInOneFlow(); break;
            case "record": RecordFlow(); break;
            case "ocr": OcrFlow(); break;
            case "scrolling": ScrollingFlow(); break;
            case "history": OpenHistory(); break;
            case "settings": OpenSettings(); break;
            case "self-timer": CaptureFullscreenTimerFlow(); break;
            case "restore-last": RestoreLastFlow(); break;
        }
    }

    // ---- Capture flows ----

    private void CaptureRegionFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            using var shot = CaptureDesktopRespectingSettings();
            var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows(), _settings, allInOne: false);
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
            HandleCapture(CaptureDesktopRespectingSettings());
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

    private async void CaptureFullscreenTimerFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            await SelfTimerWindow.RunAsync(_settings.Current.SelfTimerSeconds);
            HandleCapture(CaptureDesktopRespectingSettings());
        }
        catch (Exception ex)
        {
            Log.Error("Self-timer capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void CaptureDisplayFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            if (DisplayPickerDialog.ChooseDisplay() is SD.Rectangle r)
                HandleCapture(CaptureService.CaptureScreenRegion(r));
        }
        catch (Exception ex)
        {
            Log.Error("Display capture failed", ex);
            ShowBalloon("Capture failed", ex.Message);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void CapturePreviousFlow()
    {
        if (_captureInProgress) return;
        _captureInProgress = true;
        try
        {
            if (TryParseRegion(_settings.Current.LastCaptureRegion, out SD.Rectangle r))
                HandleCapture(CaptureService.CaptureScreenRegion(r));
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
            _captureInProgress = false;
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

            var shot = CaptureDesktopRespectingSettings();
            try
            {
                var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows(), _settings, allInOne: true);
                if (selector.ShowDialog() != true || selector.SelectedRegionPx is not SD.Rectangle rp)
                    return;
                action = selector.SelectedAction;
                regionPx = rp;

                // Crop while the frozen shot is alive (only the image actions need it).
                if (action is AllInOneAction.Capture or AllInOneAction.Ocr)
                    captured = CaptureService.Crop(shot, regionPx);
            }
            finally
            {
                shot.Dispose();
            }

            switch (action)
            {
                case AllInOneAction.Capture:
                    HandleCapture(captured!);
                    break;
                case AllInOneAction.Ocr:
                    using (captured) await RunOcrToClipboard(captured!);
                    break;
                case AllInOneAction.Record:
                    _recording?.ToggleFlow();
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
            _captureInProgress = false;
        }
    }

    /// <summary>Captures the virtual desktop, optionally hiding desktop icons first per settings.</summary>
    private SD.Bitmap CaptureDesktopRespectingSettings()
    {
        bool hid = false;
        if (_settings.Current.HideDesktopIconsDuringCapture && DesktopIcons.Visible)
        {
            DesktopIcons.Hide();
            hid = true;
            Thread.Sleep(120); // let the shell repaint before grabbing the frame
        }
        try { return CaptureService.CaptureVirtualDesktop(); }
        finally { if (hid) DesktopIcons.Show(); }
    }

    /// <summary>Every captured bitmap funnels through here: optional downscale, history, then the post-capture action.</summary>
    private void HandleCapture(SD.Bitmap bmp)
    {
        if (_settings.Current.DownscaleHiDpi)
        {
            double scale = SystemDpiScale;
            if (scale > 1.01)
            {
                int w = Math.Max(1, (int)Math.Round(bmp.Width / scale));
                int h = Math.Max(1, (int)Math.Round(bmp.Height / scale));
                try
                {
                    var scaled = BitmapEffects.Resize(bmp, w, h);
                    bmp.Dispose();
                    bmp = scaled;
                }
                catch (Exception ex) { Log.Error("HiDPI downscale failed", ex); }
            }
        }

        string? historyPath = null;
        try { historyPath = _history.Add(bmp); }
        catch (Exception ex) { Log.Error("Failed to add capture to history", ex); }

        string action = _settings.Current.PostCaptureAction;
        if (_settings.Current.AutoCopyToClipboard && action != "copy")
        {
            try { CaptureService.CopyToClipboard(bmp); }
            catch (Exception ex) { Log.Error("Auto-copy to clipboard failed", ex); }
        }

        switch (action)
        {
            case "copy":
                try { CaptureService.CopyToClipboard(bmp); ShowBalloon("WinShot", "Copied to clipboard."); }
                catch (Exception ex) { Log.Error("Copy failed", ex); }
                bmp.Dispose();
                break;
            case "save":
                SaveSilently(bmp); // takes ownership
                break;
            case "edit":
                // The window takes ownership and disposes on close — but its ctor
                // touches GDI before wiring that up, so dispose here if it throws.
                try { new EditorWindow(bmp, _settings, _history).Show(); }
                catch { bmp.Dispose(); throw; }
                break;
            case "pin":
                try { new PinWindow(bmp, _settings).Show(); }
                catch { bmp.Dispose(); throw; }
                break;
            default:
                try { ShowOverlay(bmp, historyPath); }
                catch { bmp.Dispose(); throw; }
                break;
        }
    }

    private void ShowOverlay(SD.Bitmap bmp, string? historyPath)
    {
        var overlay = new QuickActionsWindow(bmp, _settings, historyPath);
        overlay.EditRequested += OpenEditorFromOverlay;
        overlay.PinRequested += PinFromOverlay;
        overlay.OcrRequested += OcrFromOverlay;
        overlay.BackgroundRequested += BackgroundFromOverlay;
        overlay.Show();
    }

    private void SaveSilently(SD.Bitmap bmp)
    {
        try
        {
            Directory.CreateDirectory(_settings.Current.SaveFolder);
            string path = Path.Combine(_settings.Current.SaveFolder, FileNamer.Next(_settings, _settings.Current.ImageFormat));
            ImageSaver.Save(bmp, path);
            ShowBalloon("Saved", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error("Silent save failed", ex);
            ShowBalloon("Save failed", ex.Message);
        }
        finally
        {
            bmp.Dispose();
        }
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
            using (var shot = CaptureDesktopRespectingSettings())
            {
                var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows(), _settings, allInOne: false);
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
            var result = await OcrService.ExtractAsync(bmp, _settings.Current.OcrJoinLines);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Text))
                sb.Append(result.Text);
            if (result.QrCodes.Count > 0)
            {
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.Append(string.Join(Environment.NewLine, result.QrCodes));
            }

            string text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowBalloon("OCR", "No text or codes found in the selection.");
                return;
            }

            Clipboard.SetText(text);
            string title = result.QrCodes.Count > 0 ? "Text + QR code copied" : "Text copied to clipboard";
            string preview = text.Length > 80 ? text[..80] + "…" : text;
            ShowBalloon(title, preview);
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
            using (var shot = CaptureDesktopRespectingSettings())
            {
                var selector = new RegionSelectorWindow(shot, WindowEnumerator.GetTopLevelWindows(), _settings, allInOne: false);
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

    private void RestoreLastFlow()
    {
        string? path = QuickActionsWindow.PopRecentlyClosed();
        if (path is null)
        {
            ShowBalloon("Restore", "No recently closed capture to restore.");
            return;
        }
        if (LoadBitmapCopy(path) is SD.Bitmap bmp)
            ShowOverlay(bmp, path);
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
        new PinWindow(overlay.CloneImage(), _settings).Show();
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

    private void BackgroundFromOverlay(QuickActionsWindow overlay)
    {
        new BackgroundComposerWindow(overlay.CloneImage(), _settings, _history).Show();
        overlay.Close();
    }

    private void EditFromHistory(string path)
    {
        if (LoadBitmapCopy(path) is SD.Bitmap bmp)
            new EditorWindow(bmp, _settings, _history).Show();
    }

    private void PinFromHistory(string path)
    {
        if (LoadBitmapCopy(path) is SD.Bitmap bmp)
            new PinWindow(bmp, _settings).Show();
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

    private static bool TryParseRegion(string value, out SD.Rectangle rect)
    {
        rect = default;
        var parts = value.Split(',');
        if (parts.Length != 4) return false;
        if (int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) &&
            int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h) && w > 0 && h > 0)
        {
            rect = new SD.Rectangle(x, y, w, h);
            return true;
        }
        return false;
    }

    private static double SystemDpiScale
    {
        get { try { return GetDpiForSystem() / 96.0; } catch { return 1.0; } }
    }

    internal void ShowBalloon(string title, string message) =>
        _tray?.ShowBalloonTip(3000, title, message, WF.ToolTipIcon.Info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
