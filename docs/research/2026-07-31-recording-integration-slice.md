# Recording integration/settings parity slice — 2026-07-31

## Scope and protections

- Base: `origin/main` at `87bc2b73aee68cf45702d4fd9ed4043b57734bd1`.
- Worktree: `C:\Users\icalvo\.codex\worktrees\cleanshot-recording-integration-20260731\winshot`.
- Branch: `feature/cleanshot-recording-integration`.
- The installed app, main checkout, other worktrees, remote branches, releases, and installers were not changed.
- No cloud/upload feature, codec, dependency, DND manipulation, video-editor change, scrolling change, or background idle hook was added.

## Implemented

1. All-in-One Recording passes its already selected rectangle directly into the recording flow. It no longer opens a second region picker.
2. `Remember last selection` stores a separate physical-pixel recording rectangle and reuses it when enabled. An area outside the current virtual desktop falls back to the picker.
3. MP4 cross-display selections now show an OK/Cancel Windows warning. Cancel is the default. Continue records the largest overlapping display portion and uses that exact clipped rectangle for countdown, controls, dimming, and saved-area reuse. GIF keeps the full cross-display rectangle.
4. The control bar is positioned on the monitor being recorded instead of the monitor containing the cursor.
5. `Show controls while recording` now controls whether the bar appears. The recording hotkey/tray command still stops a barless recording.
6. `Display recording time` updates the tray tooltip once per second during an active recording and shows/hides the existing bar timer. The timer pauses with the recorder and creates no idle work.
7. `Dim screen while recording` creates temporary click-through dim surfaces only outside the captured rectangle. Those windows request capture exclusion, but correctness does not depend on the request because their geometry never overlaps the recorded area.
8. MP4 max resolution (`original`, `4K`, `1080p`, `720p`) and HiDPI 1x scaling now set an even, aspect-preserving output frame size. Portrait captures use portrait bounds.
9. `Record audio in mono` maps to ScreenRecorderLib's microphone mono input option only when microphone capture is enabled.
10. After-record local actions now honor completion-overlay, copy-file, and automatic-video-editor settings. Clipboard failure, toast failure, and editor failure are isolated from each other.
11. GIF max width now downscales frames only when needed. GIF quality and optimization control the encoder palette from 16–256 colors without adding a post-processing dependency or unbounded buffering.

## Deliberately not wired in this slice

- `RecordingSave`: WinShot's history and recording destination are currently the same local file. Every successful recording must be finalized to that file before history/toast/editor actions can work. Treating the checkbox as “delete when off” would risk data loss; separating private history storage from export storage needs its own approved migration slice.
- `ShowRecordingCountdown`: the per-record options dialog already supplies an explicit countdown value. Making the Settings toggle override that visible choice would be ambiguous. Existing countdown behavior is preserved.
- `DoNotDisturbWhileRecording`: excluded by scope; no OS notification-state hack was added.
- Control-bar capture exclusion is still a best-effort Windows API call from existing code. This slice does not claim the bar is always excluded when Windows rejects that request.

## Evidence and verification

- Sanitized render: `docs/evidence/recording-integration/recording-state-dim-controls-timer.png`.
- Focused Release tests: **74 passed, 0 failed**.
- Full Release tests: **359 passed, 0 failed**.
- Release build: **passed, 0 warnings, 0 errors** on the no-restore build invocation.
- Self-contained `win-x64` publish: **passed** using the local Windows SDK metadata override.
- Baseline package: **342,407,778 bytes**.
- The exact post-commit candidate size, delta, executable hash, and managed-assembly hash are recorded in the untracked build-evidence manifest under `artifacts/recording-integration/final-candidate-manifest.md`. Keeping generated publish output outside Git avoids turning the feature branch into a binary dump.
- `git diff --check`: passed.

The initial baseline test rerun omitted the Dell-specific SDK metadata override and failed before executing tests. The known clean-main baseline for this exact base commit is 328/328 from the baseline phase. All focused and final full runs above used the required override.

## Performance notes

- No new startup polling, global hook, camera access, or idle timer was added.
- The one-second tray timer exists only while a recording is active and only when its setting is enabled.
- Dim surfaces and the control bar exist only during recording.
- GIF resizing and palette work happen only on active GIF frame/encoder threads; the frame queue remains bounded at eight.
- A full candidate tray-app idle sample was not run because launching this isolated build would contend with the installed single-instance app and its live settings. The installed app was intentionally left untouched.
