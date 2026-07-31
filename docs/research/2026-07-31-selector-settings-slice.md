# Selector settings consistency slice - 2026-07-31

## Scope completed

- Region and All-In-One share one mapping for freeze screen, crosshair, crosshair mode, and magnifier behavior.
- All-In-One restore-last is gated by `AllInOneRememberLast`.
- Region and All-In-One share the same foreground recovery sequence for global-hotkey keyboard input.
- Freeze-off uses a live translucent selector surface and captures after the overlay closes; freeze-on keeps the existing physical-pixel snapshot path.

## Deferred settings work

This slice intentionally does not hide, disable, or implement the unrelated Settings backlog. The baseline had 57 `TODO: wire behavior` markers; four selector markers are now implemented, leaving 53. The 52 persisted placeholder shortcuts are unchanged. Those items require separate behavior, truthfulness, and accessibility slices.

## Final verification

- Focused selector tests: 8 passed, 0 failed.
- Full Release suite: 336 passed, 0 failed.
- Release build: passed with 0 warnings and 0 errors.
- Self-contained x64 Release publish: passed. One pre-existing `FastQuickActionsWindow.Margin` compiler warning was reported during publish; output remains untracked under `artifacts/candidate-final/`.
- `git diff --check`: passed.
- The clean `main` checkout remains at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1` with no changes.
- The installed app remains version 1.2.1 at the same source commit and SHA-256 `3F770FE547228D065F2F6525A01F28B034E8969B7A76B728452E401F0F2A8680`; it was not stopped or replaced.

## Review evidence still required

- Candidate-only Windows runs for freeze on/off, crosshair modes, magnifier on/off, remember-last on/off, and Esc/Enter after a global-hotkey launch.
- A mixed-DPI matrix at 100/125/150/200%, including a negative-coordinate monitor and a selection spanning displays.
- Current CleanShot macOS evidence for the same settings and keyboard behavior before claiming product parity.
