# WinShot

A CleanShot X-style screenshot and screen-recording tool for Windows 11. Personal
daily-driver: everything local, no cloud.

## Download

[![Download WinShot for Windows](https://img.shields.io/badge/⬇_Download-WinShot_for_Windows-2ea44f?style=for-the-badge)](https://github.com/IsaiahCalvo/WinShot/releases/latest/download/WinShot-Setup.exe)

**[Click here to download the latest WinShot installer.](https://github.com/IsaiahCalvo/WinShot/releases/latest/download/WinShot-Setup.exe)** Then open the downloaded `WinShot-Setup.exe` and follow the prompts — that's it. The button always downloads the newest version.

WinShot is not code-signed yet, so Windows SmartScreen may warn the first time you
install or run it — click **More info → Run anyway**.

## Features

- **Region / window / fullscreen capture** — `Ctrl+Shift+1` opens a frozen-screen
  selector (drag a region, or click a highlighted window), `Ctrl+Shift+2` grabs
  every monitor at once.
- **Quick-actions overlay** — every capture pops a floating thumbnail bottom-right
  with Copy / Save / Edit / Pin / OCR.
- **Annotation editor** — arrow, line, rectangle, ellipse, freehand, highlighter,
  text, blur, numbered steps, crop; undo/redo; exports at source resolution.
- **Pin to screen** — float any capture always-on-top; wheel to resize,
  Ctrl+wheel for opacity, double-click to close.
- **Screen recording** — `Ctrl+Shift+3`, MP4 (H.264, optional mic) or GIF;
  press the hotkey again or use the control bar to stop.
- **OCR** — `Ctrl+Shift+4`, select a region, text lands on the clipboard
  (Windows' built-in offline OCR).
- **Scrolling capture** — `Ctrl+Shift+5`, select a region, WinShot auto-scrolls
  and stitches the page into one tall image.
- **History** — every capture auto-saved locally (default cap 200), browsable
  from the tray menu.
- **Settings** — hotkeys, save folder, formats, FPS, launch-at-startup.

## Build & run

```powershell
dotnet build src\WinShot\WinShot.csproj -c Release
src\WinShot\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\WinShot.exe
```

x64 only (ScreenRecorderLib ships native binaries). Tests:
`dotnet test tests\WinShot.Tests\WinShot.Tests.csproj`.

Settings live in `%APPDATA%\WinShot\settings.json`; history and logs in
`%LOCALAPPDATA%\WinShot\`. The app is tray-only — look for the blue circle icon.

## Design

See [docs/specs/2026-06-12-winshot-design.md](docs/specs/2026-06-12-winshot-design.md).
