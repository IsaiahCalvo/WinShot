# Pinning parity and stability slice — 2026-07-31

## Scope and protections

- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`.
- Isolated branch/worktree only: `feature/cleanshot-pinning`.
- Product edits are confined to `Pin/FastPinWindow.cs` and `Pin/PinInteraction.cs`.
- No installed-app, main-branch, settings-schema, dependency, push, PR, merge, installer, or release change.

## Result

- Existing `PinnedRoundedCorners`, `PinnedShadow`, and `PinnedBorder` preferences now affect new pinned windows.
- Save starts in the configured local `SaveFolder`, with the existing Pictures/WinShot fallback.
- The hover toolbar and resize hit border scale with the window DPI.
- Cascaded pins are clamped inside the cursor-selected monitor, including negative-coordinate and oversized-monitor cases.
- Copy, Save, Lock, and Close expose accessible names/help and a custom accessible button tree.
- Tab/Shift+Tab reach the four actions; Enter/Space activate the focused action; focus is visibly outlined.
- Middle-click closes a pin, matching the verified CleanShot interaction.
- Existing drag, aspect-preserving resize, wheel zoom, Ctrl+wheel opacity, arrow/Shift+arrow nudge, Ctrl+L click-through, Ctrl+0 reset, double-click close, Escape close, context menu, copy, save, and close behavior remains in place.
- No polling, hook, worker, dependency, or new idle-time activity was added.

## Evidence

- [Before render](../evidence/pinning/20260731/before.png) — SHA-256 `1FCF0D1DC4267518554F03D1FD501BB3DB19C5667331449CD63EAC91776E0D4B`.
- [After render](../evidence/pinning/20260731/after.png) — SHA-256 `BA6BA8B1158330372BF35B2DE2923B9DD6AA72EEFC3BBFBA578D53A4F29FA770`.
- Both images use a synthetic sanitized capture. The after image shows rounded clipping, persistent border, the DPI-aware toolbar, hover state, and keyboard focus state. Native window shadow is verified through the window class style because an in-process bitmap render cannot capture DWM shadow composition.

## Verification

- Baseline full Release suite: 328/328 passed.
- Final focused pin tests/render harness: 27/27 passed.
- Final full Release suite: 342/342 passed.
- Release build: passed, 0 warnings, 0 errors.
- Self-contained win-x64 Release publish: passed. It reported the existing unrelated `FastQuickActionsWindow.Margin` warning.
- Publish footprint: 342,402,734 bytes baseline; 342,422,094 bytes candidate; +19,360 bytes (+0.005654%); 480 files in both.
- `git diff --check`: passed.

## Explicitly deferred / TBD

- CleanShot group-wide Hide/Show Pins and Close All Pins need shared tray/application command wiring, which this low-overlap slice intentionally did not edit.
- CleanShot's OCR context action needs shared OCR result/toast orchestration; it is deferred rather than duplicated inside the pin window.
- CleanShot uses two-finger scrolling for opacity. WinShot retains its established wheel-to-zoom and Ctrl+wheel-to-opacity behavior to preserve existing users' interaction contract.
- The full pin surface already acts as WinShot's drag affordance. A literal CleanShot-style `Drag Me` label, exact CleanShot shadow geometry, and final light/high-contrast visual tuning remain for a later evidence-backed polish pass.
