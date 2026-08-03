# Browser capture parity status

## Verified in the current vertical slice

- Visible viewport and ordinary full-page capture from the real MV3 popup.
- Browser screenshot pixels through `captureVisibleTab`, throttled below two calls/second.
- Drag selection with edge auto-scroll and restoration to the pre-picker position.
- Ctrl-hover exact element selection; yellow marks hidden scrollable content.
- Self-scrolling and nested panels, vertical/horizontal pages, sticky/fixed furniture.
- Lazy content, virtualized recycled rows, changing extents, and explicit growth/tile/time limits.
- DOM placement plus raster-overlap confidence; incomplete or low-confidence output is partial.
- IndexedDB tile storage, preview, streamed PNG, bounded-canvas JPEG/WebP, and multipage PDF
  with searchable text and working web links.
- Cancellation, navigation failure, service-worker termination, watchdog cleanup, and restart recovery.
- Tile-backed capture above 32,000 pixels without flattening to one browser canvas.
- Same-origin, cross-origin, and nested frame documents where browser site access permits.
- All-tab and URL-list batches with sequential capture, per-item results, and temporary-tab cleanup.

## Honest remaining gaps

- Infinite feeds stop at safety limits, but the popup does not yet expose per-capture limits.
- JPEG/WebP intentionally fall back to PNG/PDF for very large dimensions.
- PDF headers, footers, watermarks, and richer font/Unicode preservation are later work.
- Duplicate sibling cross-origin frames with the exact same URL are rejected when the browser
  does not expose enough identity to choose safely.
- The optional WinShot native-messaging host and tiled desktop document UI are not implemented yet.
- Store signing, Chrome/Edge store IDs, installer registration, and real installed-browser store smoke
  remain release work and require explicit authorization. Automated tests do not touch personal profiles.
