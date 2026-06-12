# CleanShot X gap analysis — 2026-06-12

Sources read: cleanshot.com (home, /features, /changelog, /docs-api, /faq).
Status vs WinShot as of this date. Cloud/sharing is permanently out of scope.

## Quick wins (small additions to existing code)

- **Self-timer capture** — delay countdown before capture.
- **Capture previous area** — repeat last region instantly via hotkey.
- **Crosshair magnifier loupe** in the region selector (we have the readout already).
- **Pixelate with randomization** — security-hardened variant of our blur.
- **Spotlight tool** — dim everything outside a region (editor).
- **Color picker / resize / rotate / flip** in the editor.
- **Crop aspect-ratio presets + edge snapping.**
- **Pin extras** — click-through "lock mode", arrow-key nudge, drag handle.
- **QR code decoding** alongside OCR (ZXing pass).
- **File naming templates** — {date}/{n}/{app}/{title} tokens.
- **WebP output + HiDPI downscale option.**
- **History type filters, restore-last-closed, time-based retention.**
- **Configurable post-capture default action** (e.g. always auto-copy, skip overlay).
- **Recording countdown + show/hide cursor toggle.**
- **Quick Look-style spacebar preview** in history.

## Medium

- **All-in-One capture mode** — one hotkey, switch area/window/fullscreen/record/OCR
  in the overlay, exact size entry, aspect lock.
- **Window capture with styled backgrounds** (transparent, padding, shadow).
- **Background/padding tool** for social-ready images.
- **System audio recording** (WASAPI loopback) — mic exists, system audio doesn't.
- **Click highlighting + keystroke overlay** in recordings.
- **Pause/resume recording** (segment files).
- **Video trim** (ffmpeg pass; full editor is large).
- **Multi-image stitching in the editor**; **non-destructive project format**
  (JSON annotation sidecar, re-edit from history).
- **Curved arrows, text styles, filled shapes, emoji.**
- **Hide desktop icons** during capture.
- **Explicit display picker** for fullscreen/record on multi-monitor.
- **winshot:// URL scheme / CLI automation** (CleanShot's docs-api equivalent).
- **Horizontal scrolling capture.**
- **OCR linebreak-handling option.**

## Large

- **Webcam overlay in recordings** (capture + live compositing + draggable preview).
- **Full video editor** (trim/quality/resolution/volume UI).

## Not applicable

- CleanShot Cloud (uploads, links, teams, branding) — out of scope by decision.
- PixelSnap / Raycast / macOS Share menu integrations — macOS ecosystem.
