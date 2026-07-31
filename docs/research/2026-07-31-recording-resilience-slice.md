# Recording resilience slice — 2026-07-31

## Protected scope

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-recording-resilience-20260731\winshot`
- Branch: `feature/cleanshot-recording-resilience`
- Base: `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`
- The installed app, source `main`, other worktrees, GitHub, and release artifacts were not changed.
- This slice does not change selector/options UI, Settings, the recording control bar, video editor, supported formats, capture inputs, or post-save behavior.

## What changed

- Same-volume finalization still uses an atomic move with collision-safe naming.
- Cross-volume finalization now copies to a private destination-side partial file, flushes it to disk, atomically renames it, and only then removes the temp source.
- Failed/cancelled copies preserve the original temp recording and clean the partial destination file.
- A destination-name race retries with the next numbered filename without overwriting either recording.
- A source-delete failure after a successful commit is treated as a successful save with a retained temp duplicate.
- MP4/GIF stop failures and finalization failures preserve temp data and show an actionable local recovery path instead of silently deleting or only logging it.
- `RecordingTempRecovery` discovers only old, non-empty, strictly named WinShot MP4/GIF temp files, supports active-path exclusions, and validates files before recovery.

## Verification

- Focused finalization/recovery tests: **12 passed**.
- Full Release suite: **338 passed, 0 failed, 0 skipped** after adding 10 tests to the 328-test baseline.
- Release build: **succeeded, 0 warnings, 0 errors**.
- Self-contained ReadyToRun publish: **succeeded** with the existing `FastQuickActionsWindow.Margin` warning.
- Publish size: baseline **342,407,758 bytes**; candidate **342,435,234 bytes**; delta **+27,476 bytes (+0.0080%)**.
- Zip size: baseline **118,100,001 bytes**; candidate **118,114,793 bytes**; delta **+14,792 bytes (+0.0125%)**.
- `git diff --check`: passed.

Coverage includes cross-volume injection, destination collisions, interrupted writes, cancellation, source-delete failure, partial cleanup, strict orphan discovery/exclusion, validated recovery, and user-facing failure text.

## Deliberate follow-up gaps

- App startup/recovery UI is not wired in this isolated slice. Later App-level integration should call discovery only after recording state is known and must exclude the active `_tempPath`.
- Settings and recording-control visibility parity remain separate work because they touch shared UI and orchestration files.
- No live second-drive/network-share smoke test was run; the cross-volume path is covered through injected file operations. A real removable, mapped, or UNC destination should be tested before release.
- Automatic retention/deletion policy for abandoned temp files needs an explicit product/privacy decision before App wiring.
- The installed app was not launched or replaced, so this slice does not claim an end-to-end live capture test.
