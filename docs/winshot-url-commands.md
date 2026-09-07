# WinShot URL and CLI commands

WinShot registers `winshot://` for local automation. If WinShot is already running, the new command is sent to the tray app and the second process exits.

Examples:

```powershell
WinShot.exe --capture-area
WinShot.exe history
WinShot.exe winshot://record?source=shortcut
Start-Process 'winshot://capture-fullscreen'
Start-Process 'winshot://capture-area?then=copy,save'
Start-Process 'winshot://capture-window?delayed=5&then=pin'
```

Supported commands:

- `capture-area`
- `capture-window`
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

## Parameters

Two query parameters change what a single capture does. Neither is persisted: the next
capture from a hotkey or the tray behaves exactly as Settings says.

| Parameter | Effect |
|---|---|
| `then=` | Replaces the after-capture set for this capture. Comma-separated: `copy`, `save`, `edit` (alias `annotate`), `pin`, `thumbnail` (alias `overlay`, the Quick Access card). Unknown words are ignored; a list with nothing usable in it leaves your settings in charge. |
| `delayed=` | Whole seconds to count down before capturing, 1–60. The on-screen countdown is the same one the self-timer uses, so it can be watched and cancelled. |

```
winshot://capture-area?then=copy,save
winshot://capture-fullscreen?delayed=10&then=thumbnail
winshot://capture-window?then=edit
```

Any other query parameter or fragment is accepted and ignored, so links like
`winshot://capture-area?from=launcher#now` still run `capture-area`.
