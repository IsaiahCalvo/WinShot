# FireShot capture parity matrix

Checked against FireShot's official [edition comparison](https://getfireshot.com/features.php), [capture overview](https://getfireshot.com/), [selection demo](https://getfireshot.com/demos/selection.php), [delay demo](https://getfireshot.com/demos/how-to-make-delayed-screenshot.php), and [release history](https://t1.getfireshot.com/updated.php?full=1&h=0).

| FireShot Lite/Standard/Pro capture capability | WinShot coverage | Verification |
| --- | --- | --- |
| Capture entire page | Implemented | Packaged MV3 full-page test |
| Capture visible area | Implemented | Popup and registered-command tests |
| Capture selected area | Implemented | Drag, edge-scroll, Ctrl-square, and Alt-full-width tests |
| Capture page element | Implemented | Ctrl-hover exact-element test |
| Capture independently scrolling area | Implemented | Nested scrolling-element test |
| Handle sticky/floating page elements | Implemented | Dynamic/fixed fixture coverage |
| Capture frames, iframes, and scrolling DIVs | Implemented | Same-origin, cross-origin, nested, and duplicate-frame tests |
| Capture all open tabs | Implemented in visible or full-page mode | Multi-tab batch test |
| Capture a list of URLs | Implemented in visible or full-page mode | URL-list batch and cleanup test |
| Delayed capture | Implemented, 0-30 seconds | Delayed-capture end-to-end test |
| Capture browser window | Implemented through WinShot desktop | Existing `capture-window` command plus extension protocol bridge |
| Extremely long page without one giant canvas | Implemented with tile storage | Greater-than-32K capture test |
| Infinite/dynamically growing page controls | Implemented with configurable time, height, tile, and growth limits | Virtual-feed and settings tests |
| Capture progress and cancellation | Implemented | Popup cancellation and exact-restoration test |
| Single-page PDF | Implemented, including pages above 32K | Long single-page PDF test |
| Multipage PDF | Implemented | PDF export tests |
| Search/select/copy PDF text | Implemented with Unicode mapping | Searchable text-layer contract test |
| Working links in PDF | Implemented | PDF annotation test |
| Custom PDF page size | Implemented: Auto, A4, Letter, Legal, or custom | Custom MediaBox test |
| Custom PDF headers, footers, and watermarks | Implemented | PDF template test |
| All tabs to one PDF in one click | Implemented | Packaged extension download test |
| HiDPI/browser zoom | Implemented using browser pixel ratio and measured tile geometry | Real-browser tiled capture tests |
| Configurable filename template | Implemented | Delayed capture/filename test |

## Scope decision

This matrix covers the full capture and PDF feature union shown across FireShot Lite, Standard, and Pro. Annotation editing, upload destinations, email/printing, and store packaging are intentionally outside this capture-engine milestone. FireShot's optional webpage JavaScript API is also tracked as future automation/integration rather than a missing capture mode.
