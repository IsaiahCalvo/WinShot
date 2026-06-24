# CleanShot-Style Parity Loop

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to run this loop. Keep each pass small, tested, and measurable.

**Goal:** Move WinShot toward CleanShot-style screenshot, recording, pinning, history, and editor quality while keeping it light enough for an always-on Windows tray app.

**Guardrail:** Do not add cloud features, tracking, heavy background services, or any idle polling that can slow the PC down.

---

## Five-Minute Loop

- [ ] Refresh current repo state with `git status --short --branch`.
- [ ] Pick one small parity gap from `docs/cleanshot-gap-analysis.md`.
- [ ] Prefer fixes that improve speed, memory use, or UI polish before adding large new features.
- [ ] Write or update the narrow test first when production code changes.
- [ ] Run the smallest useful test, then `dotnet test tests\WinShot.Tests\WinShot.Tests.csproj --no-restore`.
- [ ] For UI/performance work, also run a Release build and check idle process CPU/RAM before calling it done.
- [ ] Update `docs/cleanshot-gap-analysis.md` when a gap becomes done or changes priority.

## Lightweight Acceptance Bar

- Idle CPU should stay near zero.
- Idle memory must not grow over time.
- Capture and overlay windows should appear fast, then clean themselves up.
- Recording/scrolling work can be heavier only while the user is actively using it.
- Background cleanup should run after work, not constantly.

## Current First Pass

The current worktree already contains a large uncommitted parity batch. Stabilize that batch first:

- self-timer and previous-area capture
- all-in-one selector
- WebP save paths
- background composer
- quick preview and history filters
- recording countdown/options
- hotkey conflict UI
- memory cleanup

Do not start a new large feature until that batch builds, tests, and has a checked idle footprint.

## Progress

- 2026-06-23: Startup warmup was trimmed to native capture selectors only. Release idle sample after startup: 0 CPU seconds over 10 seconds, 125.1 MB working set, 0 MB working-set growth.
- 2026-06-23: Startup now keeps no hidden UI warmups resident. Release idle sample after startup: 0 CPU seconds over 10 seconds, 120.4 MB working set, 70.4 MB private memory, 0 MB private-memory growth.
- 2026-06-23: Region/all-in-one selectors now dispose after use instead of caching hidden full-screen forms. Live cancel smoke settled at 125.0 MB working set and 70.6 MB private memory after cleanup.
- 2026-06-23: Fast selectors now delay window-list enumeration until after first show. Region selector first-show smoke improved from 46 ms to 39 ms; post-cancel idle sample held at 124.9 MB working set and 70.5 MB private memory with 0 CPU growth.
- 2026-06-23: Drag-out temp files now get bounded on-demand cleanup from `%TEMP%\WinShot`; cleanup runs only while creating a drag file and deletes at most 50 expired files per drag.
- 2026-06-23: History age retention now also runs when new history items are added, so old captures do not accumulate if the History window is never opened.
- 2026-06-23: Generated filenames now avoid same-second collisions for silent saves and drag-out temp files by adding a numeric suffix instead of overwriting.
- 2026-06-23: OCR clipboard formatting is now centralized and QR-only captures show a QR-specific copied notification.
- 2026-06-23: OCR line-break handling is now centralized and tested for both line-preserving and paragraph-joining modes.
- 2026-06-23: Post-capture default actions now use a tested normalizer; invalid saved values fall back to overlay instead of relying on loose string checks.
- 2026-06-23: HiDPI downscale sizing now uses a tested helper so high-DPI exports shrink predictably without ever requesting zero dimensions.
- 2026-06-23: Recording option limits now use shared tested normalization, keeping countdown/cursor settings consistent across the fast dialog, old dialog, and Settings.
- 2026-06-23: History spacebar preview sizing now uses a shared tested helper, keeping Quick Look-style previews bounded without adding idle work.
- 2026-06-23: Pin-window lock, resize, opacity, and keyboard nudge behavior now uses shared tested interaction rules across the WPF and fast pin paths.
- 2026-06-23: Self-timer delay bounds now use a shared tested helper across Settings and both countdown windows, with no background polling.
- 2026-06-23: Capture-previous saved-region parsing and formatting now use one tested helper across repeat capture, region restore, and all-in-one restore.
- 2026-06-23: All-in-one selector aspect-locked drag geometry now uses one tested helper across the WPF and fast selector paths.
- 2026-06-23: Background composer canvas sizing now uses a shared tested helper for padding, inset, and fixed-ratio exports.
- 2026-06-23: Randomized pixelate now has focused bitmap tests for deterministic redo, region bounds, and undo restore behavior; the gap list now separates true missing quick wins from wired-but-needing-stabilization editor tools.
- 2026-06-23: Crop aspect-ratio presets and edge snapping now use a shared tested helper, covering free crop snapping, fixed-ratio fitting, edge translation, reverse drags, and bounds clamping.
- 2026-06-23: Fast region and all-in-one selectors now draw tested crosshair guide lines around the cursor during normal overlay paints, improving precision without screenshots, timers, or idle polling.
- 2026-06-23: Spotlight annotations now use a shared tested layout helper for editor creation, project metadata, and project reloads, clamping out-of-bounds holes without adding background work.
- 2026-06-23: Editor image resize now uses a shared tested helper for ratio-locked width/height edits, percent shortcuts, and bounds validation, including a stale percent fix for unlocked height edits.
- 2026-06-23: Editor rotate/flip now uses a shared tested bitmap transform helper, covering clockwise/counter-clockwise rotation, horizontal/vertical flips, and non-mutating source behavior for undo.
- 2026-06-23: Editor eyedropper now uses a shared tested helper for clamped pixel sampling, hex hover previews, zoom-stable preview sizing, and edge-aware swatch placement.
- 2026-06-23: Fast region and all-in-one selectors now draw a tested zoomed pixel loupe from a tiny on-demand screen sample, preserving fast first-show behavior and adding no idle polling.
- 2026-06-23: Hide-desktop-icons capture now uses a shared tested guard across desktop and region grabs, restoring icons even when capture fails and doing no background work.
- 2026-06-23: Recording click-highlight and keystroke overlays now start through a shared tested best-effort helper, so optional overlay failures are logged and cleaned up without aborting recording or adding idle hooks.
- 2026-06-23: Recording pause/resume now uses a shared tested coordinator, so overlay pause state only changes after the active recorder accepts the pause/resume transition.
- 2026-06-23: Video trim now uses a shared tested range helper for trim sliders and export math, covering minimum range, invalid values, duration clamping, and trim-from-end calculation.
- 2026-06-23: winshot:// and CLI automation now accept query strings, fragments, and simple dash-prefixed flags, with a documented supported-command list and no new idle work.
- 2026-06-23: Recording now has a `record-display` path through the existing display picker, with shared tested region normalization for even-sized H.264-safe display and region recording rectangles.
- 2026-06-23: Horizontal scrolling capture now has a direct `scroll-horizontal` tray/URL command that skips the mode chooser and uses the existing auto horizontal scroll pipeline.
- 2026-06-23: Editor filled-shape behavior now uses a shared tested brush helper for both live rectangle/ellipse drawing and project reload, stabilizing the curved-arrow/text-style/fill/emoji editor gap already present in the batch.
- 2026-06-23: MP4 recording audio flags now use a shared tested selection helper, covering microphone-only, system-only, combined, and silent recordings while keeping GIF audio disabled.
- 2026-06-23: `.winshot` project serialization now has a focused round-trip test for multiple embedded image annotations, proving `source.png`, `annotations.json`, and `images/{n}.png` survive save/load without background work.
- 2026-06-23: Window captures now have a direct `capture-window-background` tray/URL/CLI path that opens the styled background composer without changing the user's default post-capture action.
- 2026-06-23: Webcam overlay recording now has a saved size-percent control and a tested bounded layout helper, so camera setup still happens only when MP4 recording starts and the overlay is enabled.
- 2026-06-23: Webcam overlay recording now supports fullscreen camera mode through the same tested layout helper, mapped to a full-frame overlay without adding idle camera work.
- 2026-06-23: Video editor exports now support mono audio conversion through a tested audio-settings helper, covering CleanShot's stereo-to-mono editor control without adding idle work.
- 2026-06-23: Video editor exports now support Source/60/30/15 FPS choices through a tested video-settings helper, keeping export math deterministic without any background work.
