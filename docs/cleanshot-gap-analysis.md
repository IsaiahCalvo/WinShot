# CleanShot X gap analysis — 2026-06-12

Sources read: cleanshot.com (home, /features, /changelog, /docs-api, /faq). Rechecked /features and /changelog on 2026-06-23.
Status vs WinShot as of this date. Cloud/sharing is permanently out of scope.

## Quick wins (small additions to existing code)

- None currently.

## Present in current batch; needs focused stabilization

- None currently.

## Stabilized in current batch

- **History type filters, restore-last-closed, time-based retention** - implemented; age retention now prunes when new history items are added and when History opens, with no background polling.
- **File naming templates** - implemented with {date}, {time}, {n}, {app}, and {title}; silent saves and drag-out temp files now avoid overwriting same-second names by adding a numeric suffix.
- **QR code decoding alongside OCR** - implemented with ZXing; QR-only captures now use a clear "QR code copied" notification instead of saying text was copied.
- **OCR linebreak-handling option** - implemented and covered by formatter tests for preserving lines or joining them into one paragraph.
- **Configurable post-capture default action** - implemented for overlay, copy, save, edit, pin, and background actions; invalid saved values now fall back to overlay.
- **WebP output + HiDPI downscale option** - implemented; HiDPI target sizing is now centralized and tested to avoid zero-size outputs.
- **Recording countdown + show/hide cursor toggle** - implemented; countdown limits and webcam-position defaults now use shared tested normalization across settings and recording dialogs.
- **Click highlighting + keystroke overlay in recordings** - implemented; optional recording overlays now start through a tested best-effort helper that logs failures, closes partially shown overlays, and continues recording without adding idle hooks.
- **Pause/resume recording** - implemented; pause and resume now use a shared tested coordinator so recorder state changes happen before overlay state changes, and failed transitions leave state unchanged.
- **Video trim/editor basics** - implemented; the lightweight MP4 editor supports playback, trim start/end, export, resolution, quality, FPS, mute, mono audio, and volume, with trim slider/export math plus audio/video settings now using shared tested helpers.
- **Quick Look-style spacebar preview** - implemented; preview sizing is now shared and tested so images stay bounded to the current work area without extra background work.
- **Pin extras** - implemented; click-through lock mode, arrow-key nudge, drag handle, resize, and opacity controls now share tested interaction rules across both pin windows.
- **Self-timer capture** - implemented; the countdown window and Settings now share tested 1-60 second bounds, while direct non-positive calls still skip the delay.
- **Capture previous region** - implemented; saved-region parsing and formatting now use one tested helper across repeat capture, region restore, and all-in-one restore.
- **All-in-One capture mode** - implemented; one hotkey opens region/window/fullscreen/record/OCR/scroll actions, exact size entry, and aspect-locked drag math now shares tested geometry across selector paths.
- **Background/padding tool** - implemented; the composer supports styled backgrounds, padding, inset, ratios, alignment, rounded corners, and shadows, with canvas sizing now centralized and tested.
- **Pixelate with randomization** - implemented; the editor uses deterministic per-action jitter for undo/redo, with focused bitmap tests proving same-seed replay, region bounds, and restore behavior.
- **Crop aspect-ratio presets + edge snapping** - implemented; crop drag math now uses a shared tested helper for free crops, fixed ratios, edge snapping, reverse drags, and bounds clamping.
- **Fast selector crosshair guides** - implemented; the lightweight region and all-in-one selectors draw tested cursor-aligned guide lines during normal overlay paints, with no screenshots, timers, or idle polling.
- **Magnifier loupe in fast selectors** - implemented; fast region and all-in-one selectors now draw a zoomed pixel loupe from a tiny on-demand screen sample, with tested placement, source clamping, screen-coordinate labels, and no idle polling.
- **Hide desktop icons during capture** - implemented; desktop and region captures now share a tested guard that hides icons only when enabled/visible, waits for shell repaint, and restores icons even if capture fails.
- **Spotlight tool** - implemented; spotlight holes now use a shared tested layout helper for new annotations and project reloads, clamping malformed/out-of-bounds holes to valid image pixels.
- **Resize image utility** - implemented; resize dimension, ratio-lock, percent shortcut, and bounds behavior now use a shared tested helper in the editor dialog.
- **Rotate / flip image utilities** - implemented; rotate clockwise, rotate counter-clockwise, flip horizontal, and flip vertical now use a shared tested bitmap transform helper that preserves the original source for undo.
- **Color picker** - implemented; eyedropper sampling, hex preview text, hover hiding, zoom-stable preview sizing, and edge-aware swatch placement now use a shared tested helper.
- **winshot:// URL scheme / CLI automation** - implemented; the tray app registers a local protocol, forwards second-instance commands through the existing pipe, accepts URL query/fragment suffixes, and supports simple CLI flags without adding idle work.
- **Explicit display picker for fullscreen/record on multi-monitor** - implemented; fullscreen display capture and `record-display` use the lightweight monitor picker, and display recordings reuse tested even-size region normalization for H.264 safety.
- **Horizontal scrolling capture** - implemented; the chooser supports horizontal mode, image stitching has horizontal-offset coverage, and `scroll-horizontal` gives a direct tray/URL path without adding idle work.
- **Curved arrows, text styles, filled shapes, emoji** - implemented; the editor includes curved arrows with adjustable handles, text styles, emoji placement, and rectangle/ellipse fill modes, with live drawing and project reload now sharing tested fill-brush rules.
- **System audio recording** - implemented for MP4 recording; the recording dialog/settings expose microphone and system-audio toggles, and recorder input/output audio flags now use a shared tested selection helper.
- **Multi-image editor projects / non-destructive project format** - implemented; image annotations save into `.winshot` ZIP projects alongside `source.png` and `annotations.json`, with a focused round-trip test proving multiple embedded images reload with their indexes and sizes intact.
- **Window capture with styled backgrounds** - implemented; `capture-window-background` opens the existing window/region selector and sends the chosen window capture straight to the styled background composer without changing the user's default post-capture action.
- **Webcam overlay size, placement, and fullscreen mode** - implemented for MP4 recording; recording dialogs/settings expose corner/fullscreen placement and size percent, and the recorder now uses a tested bounded layout helper before touching the camera.

## Medium

- None currently.

## Large

- **Webcam overlay polish** (shape options and draggable preview before recording).
- **Full video editor polish** (editing niceties beyond trim/playback/quality/FPS/resolution/audio controls).

## Not applicable

- CleanShot Cloud (uploads, links, teams, branding) — out of scope by decision.
- PixelSnap / Raycast / macOS Share menu integrations — macOS ecosystem.
