# FireShot capture parity matrix

Checked against FireShot's official [edition comparison](https://getfireshot.com/features.php), [capture overview](https://getfireshot.com/), [selection demo](https://getfireshot.com/demos/selection.php), [delay demo](https://getfireshot.com/demos/how-to-make-delayed-screenshot.php), and [release history](https://t1.getfireshot.com/updated.php?full=1&h=0).

| FireShot Lite/Standard/Pro capture capability | WinShot coverage | Verification |
| --- | --- | --- |
| Capture entire page | Implemented | Packaged MV3 full-page test |
| Capture visible area | Implemented | Popup and registered-command tests |
| Capture selected area | Implemented | Drag, edge-scroll, Ctrl-square, and Alt-full-width tests; adversarial tests cover efficient tiling of a region released off-screen, document anchoring on pre-scrolled pages, long-drag (>9s) stability, and no event leakage through the modal overlay |
| Capture page element | Implemented | Ctrl-hover exact-element test; adversarial tests cover elements inside open shadow roots and position:fixed elements (captured in place, no scroll) |
| Capture independently scrolling area | Implemented | Nested scrolling-element test; transform-scaled scroll containers are honestly reported partial (known limitation) |
| Handle sticky/floating page elements | Implemented | Captured-pixel test proves header/footer appear exactly once, including across growth passes on growing pages (stale-footer-band regression test) |
| Capture frames, iframes, and scrolling DIVs | Implemented | Same-origin, cross-origin, nested, duplicate-frame, and shadow-hosted-iframe tests |
| Capture all open tabs | Implemented in visible or full-page mode | Multi-tab batch test; adversarial tests cover honest per-tab skip reasons for restricted tabs and refusal to start while a capture is running |
| Capture a list of URLs | Implemented in visible or full-page mode | URL-list batch and cleanup test; 20-URL cap and disposable-tab cleanup adversarially tested |
| Delayed capture | Implemented, 0-30 seconds | Delayed-capture end-to-end test; Alt-hold one-off delay tested (countdown visible, delay not persisted) |
| Capture browser window | Available through WinShot desktop | Desktop command is tested; extension custom-protocol launch still needs live manual acceptance |
| Extremely long page without one giant canvas | Implemented with tile storage | Greater-than-32K capture test |
| Infinite/dynamically growing page controls | Implemented with configurable time, height, tile, and growth limits | Growing-page, virtual-feed, and settings tests |
| Capture progress and cancellation | Implemented | Popup cancellation and exact-restoration test; badge lifecycle and repeat-vs-stop hotkey behavior adversarially tested; mid-run history deletion now cancels the capture instead of corrupting the record |
| Single-page PDF | Implemented, including pages above 32K | Long single-page PDF test |
| Multipage PDF | Implemented | PDF export tests |
| Search/select/copy PDF text | Implemented with Unicode mapping | Independent PDF reader extracts Latin, Japanese, Arabic, and emoji/supplementary-plane text (surrogate-aware ToUnicode CMap) |
| Working links in PDF | Implemented | Independent PDF reader extracts the link annotation; javascript:/data: hrefs are verified never to become annotations |
| Custom PDF page size | Implemented: Auto, A4, Letter, Legal, or custom | Custom MediaBox test; adversarial test verifies clamping (0.1in floors to 1in, 100in max) and honest {pages} pagination |
| Custom PDF headers, footers, and watermarks | Implemented | PDF template test; injection-proof against hostile titles, and watermarks at the string-escape boundary no longer corrupt the content stream |
| All tabs to one PDF in one click | Implemented | Packaged extension download test |
| HiDPI/browser zoom | Browser zoom implemented; DPR above 1 has an open bug | Browser zoom is e2e-tested at 125%; the DPR-2 e2e test fails (output is 1x, not 2x) — the project's known open DPI bug, needs live HiDPI investigation |
| Configurable filename template | Implemented | Delayed capture/filename test; hostile-title tests verify %-tokens inside page titles are not expanded and long names never end in an illegal trailing dot |

Verification status last reconciled against the 2026-08-20 adversarial QA pass (five adversarial-*.spec.mjs suites, 79 tests total, 78 passing); full findings in `.claudehelper/extension-qa-report.md` at the repo root.

## Scope decision

This matrix covers the browser-native capture and PDF feature union shown across FireShot Lite, Standard, and Pro. Browser-window capture is supplied by WinShot desktop, but the extension-to-desktop launch is not yet a dependable acknowledged bridge. Annotation editing, upload destinations, email/printing, and store packaging are intentionally outside this capture-engine milestone. FireShot's optional webpage JavaScript API is also tracked as future automation/integration rather than a missing capture mode.
