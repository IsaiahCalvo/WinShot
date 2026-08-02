# WinShot local parity candidate — review index

Candidate: `integration/cleanshot-local-parity`

Protection: local worktree only. Main, GitHub, the installed app, installer, and release state are unchanged.

## Review surfaces

| Surface | Candidate state | Evidence and notes |
| --- | --- | --- |
| Capture selection | Saved selector options now control the live selector consistently. | `2026-07-31-selector-settings-slice.md` |
| Quick Access | CleanShot-like compact preview; pin top-left, close top-right, edit bottom-left, supplied Save icon bottom-right, Copy centered. | `docs/evidence/quick-access-overlay/`; `2026-07-31-quick-access-overlay-slice.md` |
| Annotation | Existing 17 tools preserved; hierarchy, keyboard copy, project roundtrip, zoom, pin/copy, DPI and accessibility polished. | `docs/evidence/annotation/`; `docs/specs/2026-07-31-annotation-editor-slice.md` |
| Pinning | Persistent corner/shadow/border settings, DPI-scaled chrome, monitor clamping, keyboard/accessibility, middle-click close. | `docs/evidence/pinning/`; `2026-07-31-pinning-parity-slice.md` |
| History | Safer delete/copy/pin behavior plus incremental 200-item pages for large local libraries. | `docs/evidence/history/`; `docs/evidence/history-performance/`; history slice reports |
| Scrolling capture | Horizontal relock/reverse recovery, geometric growth, incremental preview, 512 MiB guard, DPI/accessibility improvements. | `docs/evidence/scrolling-capture/`; `2026-07-31-scrolling-capture-slice.md` |
| Recording | Remembered area, no duplicate All-in-One picker, monitor-aware controls, timer/dimming, HiDPI/max resolution, mono audio, GIF tuning, local after-actions, safe finalization and startup recovery. | `docs/evidence/recording-integration/`; recording slice reports |
| Video editor | Keyboard trim/sliders, accessible focus, clear Cancel and Trim & Export actions, cancellation cleanup and surfaced failures. | `docs/evidence/video-editor/`; `2026-07-31-video-editor-slice.md` |
| Settings | Truthful controls only; Quick Access, recording, GIF, and pin options re-enabled after their runtime behavior was integrated. | `docs/evidence/integration-settings/after/`; `2026-07-31-settings-truthfulness-slice.md` |

## Combined gate

- 493/493 Release tests passed.
- Release build passed with 0 warnings and 0 errors.
- CI-equivalent self-contained `win-x64` publish passed.
- 480 files, 342,738,118 bytes; +37,408 bytes (+0.0109%) over the pre-Settings integration candidate.
- `git diff --check origin/main` passed.
- No cloud service or new dependency was added.

## Deliberate limits before approval

- The isolated executable was not started through normal `App.OnStartup`: doing so while the installed copy runs would collide with the single-instance pipe/hotkeys, while stopping it would let the candidate rewrite protocol/startup registration to the worktree path.
- Fresh WPF window renders and interaction tests cover the candidate without those external side effects.
- Live Chrome/Excel/Electron scrolling, mixed-DPI cross-monitor behavior, Narrator, and actual microphone/system-audio recording remain installation-stage checks.
- `RecordingSave`, the duplicate global countdown toggle, and Do Not Disturb remain hidden because their intended behavior is still ambiguous or not implemented.

Nothing in this candidate automatically merges, installs, updates, or releases WinShot.
