# Quick Access overlay parity slice - 2026-07-31

## Protected scope

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-quick-access-20260731\winshot`
- Branch: `feature/cleanshot-quick-access-overlay`
- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`
- The installed WinShot app and `main` were not changed, stopped, replaced, or launched from this candidate.
- Nothing was pushed, merged, tagged, installed, or released.

## Implemented behavior

- Idle is only a compact rounded capture thumbnail; the OS-owned drop shadow sits outside the client render.
- Hover softens and darkens the thumbnail, then reveals one centered 58 x 29 Copy pill plus 22 x 22 Pin, Close, Annotate, and Save circles at 100% DPI.
- Pin is top-left, Close top-right, Annotate bottom-left, and Save bottom-right. Cloud is absent.
- Save uses the user's supplied `download-window-svgrepo-com.svg` path as an embedded DPI-scaled asset.
- OCR and Background remain available through right-click, `Shift+F10`/Menu, and the existing `O`/`B` keys.
- `Ctrl+C` copies and closes; `Ctrl+Alt+C` or Alt-click Copy keeps the overlay open. Existing single-key `C` remains compatible and keeps it open.
- `Ctrl+S` saves, `Ctrl+E` annotates, `Ctrl+W`/Esc closes, and `P` pins. Tab/Shift+Tab plus Enter/Space operate the visible actions.
- Custom-painted actions now expose accessible names, help/tooltips, roles, bounds, and keyboard focus.
- Bottom-right is the new default and invalid-value fallback. Left, right, top, bottom, bottom-left, and bottom-right remain supported.
- Move-to-active-display chooses the cursor display when enabled and the primary display when disabled.
- The size slider changes the card footprint. Layout and work-area placement scale for per-monitor DPI and clamp on negative-coordinate monitors.
- Auto-close now honors enabled/seconds plus Save and Close, Copy and Close, or Close. Hover and the overflow menu pause the one-shot timer.
- Drag-out now honors Close after dragging; Alt keeps the card open.
- Save now honors direct export versus always ask; Alt forces destination selection. Auto-save uses the configured export folder without showing a dialog.
- Multiple cards remain vertically stacked, and expanding one collapses the others.

## Changed files

- `src/WinShot/Overlay/FastQuickActionsWindow.cs`
- `src/WinShot/Overlay/QuickAccessOverlayLayout.cs`
- `src/WinShot/Core/SettingsService.cs`
- `src/WinShot/SettingsUi/SettingsWindow.xaml.cs`
- `src/WinShot/Assets/download-window-svgrepo-com.svg`
- `src/WinShot/WinShot.csproj`
- `tests/WinShot.Tests/QuickAccessOverlayLayoutTests.cs`
- `tests/WinShot.Tests/QuickAccessOverlayInteractionTests.cs`
- `tests/WinShot.Tests/QuickAccessOverlayRenderHarness.cs`
- `tests/WinShot.Tests/ThemedWindowTests.cs`

## Render evidence

- Before idle: `docs/evidence/quick-access-overlay/20260731/before/idle.png`
- Before hover: `docs/evidence/quick-access-overlay/20260731/before/hover.png`
- After idle: `docs/evidence/quick-access-overlay/20260731/after/idle.png`
- After hover: `docs/evidence/quick-access-overlay/20260731/after/hover.png`
- User-directed final idle: `docs/evidence/quick-access-overlay/20260731/after-user-layout/idle.png`
- User-directed final hover: `docs/evidence/quick-access-overlay/20260731/after-user-layout/hover.png`

The PNGs use synthetic artwork and an in-process client render. They contain no captured user content, do not launch the candidate, and do not include the external OS drop shadow.

## Verification

- Focused overlay tests: **29 passed, 0 failed**.
- Full Release suite: **357 passed, 0 failed, 0 skipped**.
- Test compilation still reports the pre-existing nullable warning in `EditorClipSweep.cs:38`; this slice did not change that file.
- Test result: `artifacts/quick-access/test-results/quick-access-release.trx`
- Release build: **0 warnings, 0 errors**.
- Release self-contained `win-x64` publish succeeded with `CsWinRTWindowsMetadata=C:\Users\icalvo\.nuget\packages\microsoft.windows.sdk.net.ref\10.0.19041.56\winmd`.
- Publish: `artifacts/quick-access/publish/WinShot/`
- Zip: `artifacts/quick-access/WinShot-win-x64.zip`
- Baseline publish: 480 files, 342,407,758 bytes.
- Candidate publish: 480 files, 342,439,634 bytes.
- Impact: **+31,876 bytes (+0.0093%) unpacked** and **+17,258 bytes (+0.0146%) zipped**.
- Final user-directed publish impact: **+40,628 bytes (+0.0119%) unpacked** versus baseline.
- Supplied Save SVG SHA-256: `504FBB34A1EF0891D699B16ACFA17585B901BE9E785A4496DB6818679E28FC3A`.
- No package dependency was added. There is no idle polling; the only new timer is one-shot and exists only when auto-close is enabled.

## Deferred review

- No requested Quick Access shell setting remains deferred.
- Screenshot/recording After Capture matrix settings are a broader workflow and were not changed in this slice.
- A newest-item entrance animation/indicator was intentionally not added because exact current motion remains unknown and the slice does not need recurring work.
- A separately authorized candidate run is still required for actual OS shadow, focus, Narrator/high contrast, mixed-DPI displays, drag/drop, clipboard, save dialogs, and timer actions. The sanitized renders are not a live-runtime claim.
