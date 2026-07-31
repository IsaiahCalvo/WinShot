# WinShot local history parity and stability slice

Date: 2026-07-31
Branch: `feature/cleanshot-history`
Base: `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`

## Outcome

This isolated slice keeps WinShot history local-only and preserves its existing grouping,
filters, watcher refresh, drag-out, Copy/Edit/Pin/Open/Reveal/Delete actions, Space preview,
keyboard shortcuts, retention pruning, and chronological naming.

Implemented:

- A card is removed only after its history file is confirmed absent from disk.
- Delete failures stay visible and show a Windows warning with the failure detail.
- Clipboard image copies clone the decoded bitmap before the file stream closes.
- Pin is hidden and runtime-blocked for video and GIF items; still images keep Pin.
- History cards, filters, primary actions, context actions, count, and window have explicit
  accessible names/help text.
- Keyboard focus has a visible accent treatment and exposes the same card action row as hover.
- History opens centered on the monitor containing the cursor, using that monitor's working
  area and effective DPI.

No cloud behavior, new dependency, proprietary asset, settings migration, or installed-app
change was introduced.

## CleanShot reference disposition

Official CleanShot material describes Capture History filters, removal, up-to-one-month
retention, multi-item restore, double-click-to-Annotate, and external files opened with
CleanShot appearing in history.

WinShot's existing local grouping, live watcher, quick preview, and type filters were preserved.
Multi-item restore, external-file ingestion, and a one-month preset are deferred because their
safe implementation crosses shared `App.xaml.cs` / settings ownership outside this slice.
WinShot currently double-clicks through the existing Open action; changing it to always open
Annotate is a visible behavior decision and was not inferred here.

## Verification

- Focused history tests: 18 passed, 0 failed.
- Full Release suite: 340 passed, 0 failed.
- Release build: succeeded, 0 warnings, 0 errors.
- Self-contained `win-x64` Release publish: succeeded.
- Candidate executable SHA-256:
  `B4E8AD20A3BE6C9D29F1D90FA4F8351E9F7F621C76EF9B0413B1A9E01FD4DF60`.
- `git diff --check`: passed.

Package comparison, using the same self-contained ReadyToRun publish command for base and
candidate:

| Measurement | Base | Candidate | Delta |
| --- | ---: | ---: | ---: |
| Published files | 480 | 480 | 0 |
| Total bytes | 342,403,178 | 342,421,870 | +18,692 (+0.0055%) |

## Visual evidence

Sanitized WPF renders were produced from four generated fixtures; they contain no user content.

- Before idle: `docs/evidence/history/20260731/before/history-idle.png`
- Before keyboard focus: `docs/evidence/history/20260731/before/history-keyboard-focus.png`
- After idle: `docs/evidence/history/20260731/after/history-idle.png`
- After keyboard focus: `docs/evidence/history/20260731/after/history-keyboard-focus.png`

The after-focus render shows the keyboard-reachable Copy/Edit/Pin/Delete row. The after idle
render keeps the existing uncluttered card layout. Model and XAML tests verify that video/GIF
items do not expose Pin.

Evidence SHA-256:

| File | SHA-256 |
| --- | --- |
| Before idle | `DA3AA57059BC05F1D163323077BF423212C05F27B929B560512D8AF420FF10B3` |
| Before keyboard focus | `DA3AA57059BC05F1D163323077BF423212C05F27B929B560512D8AF420FF10B3` |
| After idle | `A45341D3D2A6180451FC5D9390C5BD8D029114DA2C0D160CF65B0C49F3D251E7` |
| After keyboard focus | `41D3E151E72C9B97D0FBA58BCBD5AEBC3D106F8F1546DC538776C391D55BB3C2` |

## Deliberate deferrals

- The grouped WPF `WrapPanel` is still non-virtualized. Replacing it safely for a 5,000-item
  library requires a dedicated layout/performance slice with interaction and grouping parity;
  this slice does not claim a 5,000-item responsiveness improvement.
- History disable/clear-all, privacy messaging, retention preset UX, and settings migration are
  shared-file work and remain deferred.
- Multi-item restore and external-file ingestion remain deferred as described above.

The installed WinShot folder, installed process, main checkout, remote repository, and release
state were not modified.
