# Browser capture parity status

## Verified in the current vertical slice

- Visible viewport and ordinary full-page capture from the real MV3 popup.
- Browser screenshot pixels through `captureVisibleTab`, throttled below two calls/second.
- Drag selection with edge auto-scroll and restoration to the pre-picker position.
- Ctrl-hover exact element selection; yellow marks hidden scrollable content.
- Self-scrolling and nested panels, vertical/horizontal pages, sticky/fixed furniture.
- Lazy content, virtualized recycled rows, changing extents, and explicit growth/tile/time limits.
- DOM placement plus raster-overlap confidence; incomplete or low-confidence output is partial.
- IndexedDB tile storage, preview, streamed PNG, bounded-canvas JPEG/WebP, multipage PDF.
- Cancellation, navigation failure, service-worker termination, watchdog cleanup, and restart recovery.
- Tile-backed capture above 32,000 pixels without flattening to one browser canvas.

## Honest remaining gaps

- Cross-origin iframe hidden content: visible pixels work; scrolling inside the frame needs
  optional host access and frame-specific orchestration.
- Same-origin iframe deep capture and nested frame picker coordination need dedicated tests.
- Infinite feeds stop at safety limits, but the popup does not yet expose per-capture limits.
- All-tabs and URL batches are not yet in the UI; they require an explicit optional permission flow.
- JPEG/WebP intentionally fall back to PNG/PDF for very large dimensions.
- PDF is image-based; searchable text, live links, headers, footers, and watermarks are later work.
- The optional WinShot native-messaging host and tiled desktop document UI are not implemented yet.
- Store signing, Chrome/Edge store IDs, installer registration, and real installed-browser store smoke
  remain release work and require explicit authorization. Automated tests do not touch personal profiles.
