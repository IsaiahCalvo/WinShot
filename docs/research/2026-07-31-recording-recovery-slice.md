# Recording startup recovery slice — 2026-07-31

## Protected scope

- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-recording-recovery-20260731\winshot`
- Branch: `feature/cleanshot-recording-recovery`
- Base: `7ed21de2540a657030b5a46bb392acc6e3e070de`
- The installed app, source `main`, other worktrees, GitHub, and release artifacts were not changed.
- This slice does not change recording selection/options, Settings, the control bar, codecs, capture inputs, video editor behavior, themes, or retention policy.

## What changed

- Startup recovery runs only after Settings and History initialization, at app idle, and never while capture/recording/finalization or an incoming startup command is active.
- Discovery still uses the strict `RecordingTempRecovery` rules: top-level `recording-{GUID}.mp4/.gif`, non-empty, at least 30 minutes old, with the active temp path explicitly excluded.
- A single Windows-native dialog lists all candidates and defaults to **Keep for later**. Closing, Escape, or Enter leaves every source untouched.
- **Recover** finalizes each candidate into the configured save folder with collision-safe unique naming, adds the final file to local History, and shows the existing local recording toast. Recovered MP4 files keep the toast's Edit action.
- A failed recovery displays and logs the exact retained temp-file location plus a practical next step. No startup path deletes an abandoned recording, and no retention policy was added.
- The startup coordinator is dependency-injected and guarded once per App instance so discovery, decisions, failures, History warnings, and prompt count are deterministic in tests.

## Verification

- Focused finalization/recovery tests: **19 passed**.
- Full Release suite: **345 passed, 0 failed, 0 skipped**.
- Release build: **succeeded**. The only remaining app warning is the pre-existing `FastQuickActionsWindow.Margin` warning.
- Self-contained ReadyToRun publish: **succeeded**.
- Publish size versus base `7ed21de`: **342,435,234 → 342,472,938 bytes**, delta **+37,704 bytes (+0.0110%)**.
- Zip size versus base `7ed21de`: **118,114,793 → 118,130,139 bytes**, delta **+15,346 bytes (+0.0130%)**.
- `git diff --check`: passed.

Coverage includes initialization/recording gating, strict active-path exclusion, recover, keep, failed-save source preservation, History-copy failure semantics, unique naming, and one-prompt-per-start behavior.

## Live verification boundary

- The candidate executable was not launched because the installed WinShot instance was not stopped or replaced. Startup behavior is covered through deterministic coordinator tests, WPF compilation, full tests, and Release publish. A user-approved candidate launch should be the next integration check before merge.
