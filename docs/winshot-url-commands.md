# WinShot URL and CLI commands

WinShot registers `winshot://` for local automation. If WinShot is already running, the new command is sent to the tray app and the second process exits.

Examples:

```powershell
WinShot.exe --capture-area
WinShot.exe history
WinShot.exe winshot://record?source=shortcut
Start-Process 'winshot://capture-fullscreen'
```

Supported commands:

- `capture-area`
- `capture-fullscreen`
- `capture-display`
- `capture-previous`
- `capture-window-background`
- `all-in-one`
- `record`
- `record-display`
- `ocr`
- `scrolling`
- `scroll-horizontal`
- `history`
- `settings`
- `self-timer`
- `restore-last`
- `exit`

Query strings and fragments are accepted and ignored, so links like `winshot://capture-area?from=launcher#now` still run `capture-area`.
