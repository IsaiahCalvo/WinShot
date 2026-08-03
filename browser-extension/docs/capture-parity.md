# Browser capture parity status

## Verified capture surface

- Visible viewport, full page, dragged selection, exact element, and independently scrolling-area capture.
- Selection edge auto-scroll, Ctrl square selection, Alt full-page-width selection, and Ctrl-hover element selection.
- Sticky/fixed-element handling, lazy content, virtualized rows, changing page extents, horizontal pages, and configurable delay and safety limits.
- Same-origin, permitted cross-origin, nested, and duplicate-URL sibling frames using exact frame identity.
- All-open-tab and URL-list batches in visible or full-page mode, plus one-click all-tabs-to-one-PDF.
- Browser-window capture exists in WinShot desktop. The extension can request it through `winshot://capture-window`, but that external-app handoff still requires live manual acceptance and does not return a success acknowledgement.
- Tile-backed captures above 32,000 pixels, cancellation, navigation failure, service-worker recovery, and exact page restoration.
- PNG, bounded-canvas JPEG/WebP, searchable linked PDF, single- or multipage PDF, standard/custom page sizes, headers, footers, and watermarks. An independent PDF reader extracts the Latin, Japanese, and Arabic text and link annotations in the end-to-end suite.

The browser-native paths are covered by unit tests, packaged Manifest V3 end-to-end tests in disposable Chrome-for-Testing profiles, and Chrome/Edge fixture smoke tests. Desktop regressions are checked separately; selectors, global hotkeys, and the external-protocol handoff cannot be called fully end to end without interacting with the real Windows desktop.

See [fireshot-capture-matrix.md](fireshot-capture-matrix.md) for the feature-by-feature comparison.

## Deliberate boundaries

- Editing/annotation parity is a separate workstream, as requested.
- FireShot's webpage JavaScript API is an automation/integration surface, not a capture mode. WinShot currently exposes browser commands and the desktop protocol; a public webpage API is not included here.
- GIF/BMP, clipboard, email, printing, cloud upload, and store publishing are output/distribution features rather than capture-engine features.
- Very large JPEG/WebP exports fall back to PNG/PDF because browsers cannot reliably allocate one giant canvas.
- DRM/protected browser surfaces can be blank by browser policy. Restricted internal pages receive an explicit fallback instead of a false success.
- Native editor handoff, store signing/IDs, installer registration, and installed-store-browser smoke remain release/integration work. Automated tests never use personal browser profiles.
- A dependable extension-to-desktop connection should use Chrome Native Messaging so WinShot can acknowledge success, failure, progress, and cancellation. The current custom URL is only a convenience launch path.
