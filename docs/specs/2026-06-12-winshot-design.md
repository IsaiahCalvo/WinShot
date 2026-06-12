# WinShot — CleanShot X equivalent for Windows

**Date:** 2026-06-12 · **Owner:** Isaiah · **Status:** Approved (chat, 2026-06-12)

## Goal

A personal daily-driver screenshot/recording tool for Windows 11 with CleanShot X's
core workflow: hotkey → capture → floating quick-actions thumbnail → annotate / pin /
copy / save. Everything except cloud upload/sharing.

## Scope (v1)

- Region / window / fullscreen screenshot capture via global hotkeys
- Frozen-screen region selector with window snapping and live dimensions
- Floating quick-actions overlay (thumbnail bottom-right: Copy, Save, Edit, Pin, OCR, Close)
- Annotation editor: arrow, line, rect, ellipse, freehand, highlighter, text,
  blur/pixelate, numbered steps, crop; color/stroke options; undo/redo; copy/save
- Pin to screen: floating always-on-top image windows (drag, wheel-resize, dbl-click close)
- Screen recording: region/fullscreen to MP4 (H.264, optional audio) and GIF, with control bar
- OCR text capture (Windows built-in OCR engine, offline) → clipboard
- Capture history: auto-save all captures locally, browsable thumbnail grid
- Settings window: hotkeys, save folder, formats, recording options, launch-at-startup
- Scrolling capture: auto-scroll + stitch (built last; known-flaky feature class)

**Out of scope:** cloud upload, sharing links, annotations on video, multi-user anything.

## Stack & key decisions

- **.NET 8 WPF**, TFM `net8.0-windows10.0.19041.0` (unlocks WinRT: `Windows.Media.Ocr`).
  Chosen over Electron/Tauri for first-class global hotkeys, transparent overlays,
  capture APIs, free built-in OCR, and low idle footprint for an always-on tray app.
- **Tray icon:** WinForms `NotifyIcon` (`UseWindowsForms=true`) — battle-tested.
- **Hotkeys:** Win32 `RegisterHotKey` through a hidden message window.
  Defaults: `Ctrl+Shift+1` region/window, `Ctrl+Shift+2` fullscreen,
  `Ctrl+Shift+3` record, `Ctrl+Shift+4` OCR, `Ctrl+Shift+5` scrolling capture.
- **Screenshot capture:** GDI `CopyFromScreen` of the full virtual desktop
  (multi-monitor, PerMonitorV2 DPI-aware manifest).
- **Region selector:** borderless topmost window spanning the virtual desktop showing
  the frozen capture; drag = region, hover = snap to window (EnumWindows +
  `DWMWA_EXTENDED_FRAME_BOUNDS`), Esc cancels.
- **Recording:** `ScreenRecorderLib` NuGet (wraps Windows.Graphics.Capture + Media
  Foundation) for MP4; custom GDI frame grab (~12 fps) + own GIF encoder for GIF.
- **OCR:** `Windows.Media.Ocr`, result to clipboard + toast.
- **Scrolling capture:** `SendInput` wheel events + pure-C# overlap stitching
  (row-correlation), no OpenCV dependency.
- **Persistence:** settings JSON in `%APPDATA%\WinShot`; history + logs in
  `%LOCALAPPDATA%\WinShot`; history auto-prunes past 200 items.

## Architecture

Single project `src/WinShot/`. Feature folders, each independently understandable:

- `Core/` — HotkeyManager, CaptureService, SettingsService, HistoryService, WindowEnumerator, Log
- `Capture/` — RegionSelectorWindow
- `Overlay/` — QuickActionsWindow
- `Editor/` — EditorWindow + tool classes (one class per annotation tool)
- `Pin/` — PinWindow
- `Recording/` — RecorderService, GifRecorder, RecordingControlBar
- `Ocr/` — OcrService
- `History/` — HistoryWindow
- `Scrolling/` — ScrollingCaptureService, ImageStitcher
- `SettingsUi/` — SettingsWindow

`App.xaml.cs` owns the tray icon, wires hotkeys → actions, and enforces single instance
(named mutex). Data flow: hotkey → CaptureService produces a Bitmap → RegionSelector
crops → QuickActionsWindow offers actions → Editor/Pin/OCR consume the bitmap;
HistoryService persists every capture as a side effect.

## Error handling

Global `DispatcherUnhandledException` + `AppDomain` handler → log to
`%LOCALAPPDATA%\WinShot\logs\winshot.log`, tray balloon with short message, app stays
alive. Recording errors finalize the partial file and notify. OCR unavailable
(no language pack) degrades to a clear message, not a crash.

## Build phases

1. **Skeleton + capture** — tray, hotkeys, settings/history services, region selector,
   quick-actions overlay, copy/save. *Usable screenshot tool at end of phase.*
2. **Editor + Pin** — full annotation suite, pin windows.
3. **Recording** — MP4 + GIF + control bar.
4. **OCR, History window, Settings window, startup toggle.**
5. **Scrolling capture** — riskiest, isolated last.

## Testing

Personal tool: unit tests for pure logic (ImageStitcher, settings round-trip, history
pruning); `dotnet build` clean + manual smoke checklist per phase (capture all three
modes, annotate, record 10s, OCR a dialog, stitch a long page).
