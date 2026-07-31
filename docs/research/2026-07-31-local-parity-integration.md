# Local CleanShot parity integration candidate

Date: 2026-07-31

## Protection and scope

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-integration-20260731\winshot`
- Branch: `integration/cleanshot-local-parity`
- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`
- Local-only candidate. Main, the installed app, GitHub, PRs, tags, installers, and releases were not changed.
- The Settings truth follow-up is intentionally not included yet.

## Integrated slices

The following source commits were cherry-picked in order:

1. `60b78f2` - quick-access overlay
2. `6e36c4d` - selector/settings consistency
3. `22f9e39`, `39a1a77` - annotation editor parity and text-size correction
4. `609b398` - pinned screenshot interactions
5. `b473cc1`, `a2a413d` - local history and large-library scalability
6. `df4e5ac` - scrolling capture
7. `7ed21de`, `d928c9d` - recording finalization resilience and startup recovery
8. `f81552a` - video editor accessibility and export recovery
9. `22a01eb` - recording settings integration

## Semantic reconciliation

Only `RecordingController.cs` required a content-conflict resolution. The resolution retained both sides of the behavior:

- Startup recovery keeps `ActiveTempPath` and `BlocksStartupRecovery`, so it cannot compete with a live chooser, recorder, stop, or finalization path.
- Tray elapsed-time updates keep `RecordingElapsedChanged`.
- Recording finalization still uses the safe same-volume move and cross-volume copy/flush/atomic-rename flow. A committed destination is not destroyed if the temp duplicate cannot be removed.
- Recorder and finalization failures preserve recoverable temp files and show the real local path.
- Area reuse, All-in-One region handoff, cross-display handling, dimming, control/timer visibility, HiDPI/max-resolution scaling, mono input, GIF size/quality/optimization, and after-record actions remain wired.
- Recovery startup, dim overlay, elapsed timer, and control bar teardown all remain part of shutdown and failure cleanup.

`SettingsService.cs` merged without a text conflict. The combined persistence tests cover selector, quick-access, history, recording, GIF, scaling, and after-action settings together.

No product decision was required during reconciliation. The MP4 single-display limitation continues to be disclosed before capture; GIF retains cross-display capture.

## Validation

All commands used the local SDK metadata override:

`C:\Users\icalvo\.nuget\packages\microsoft.windows.sdk.net.ref\10.0.19041.56\winmd`

- Affected focused Release tests: **332 passed, 0 failed, 0 skipped**.
- Complete Release suite: **490 passed, 0 failed, 0 skipped**.
- Release app build: **passed, 0 warnings, 0 errors**.
- CI-equivalent self-contained `win-x64` Release publish: **passed**.
- Publish output: 480 files, 342,700,710 bytes.
- Published `WinShot.exe` SHA-256: `8EF81EE6115C487838C2866CAEAA0715563D4EA46E9D32AD81A1082582741C23`.
- `git diff --check origin/main`: passed.

The candidate was not launched because WinShot's single-instance path could interact with the protected installed app. Runtime/install verification remains a separate approval gate.

## Repository hygiene

- No build output, executable, DLL, PDB, archive, installer, video, audio, log, database, or private capture is tracked.
- The 36 newly tracked PNG files are small, sanitized UI render-harness evidence under `docs/evidence/`; all were visually reviewed together. They contain only synthetic labels/content.
- The supplied save SVG is intentionally tracked as the quick-access Save icon source.
- `publish/`, `bin/`, and `obj/` remain ignored and local only.

## Next local integration step

Cherry-pick the separately verified Settings truth follow-up when it is ready, rerun the same focused/full/build/publish gates, and keep installation, push, PR, merge, tag, and release behind explicit approval.
