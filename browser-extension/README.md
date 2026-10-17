# WinShot Capture browser extension

WinShot Capture is a local-first Manifest V3 extension. It captures real browser pixels,
stores large captures as tiles, validates coverage and overlap confidence, and exports
PNG, JPEG, WebP, GIF, BMP or PDF without a cloud service. WinShot desktop integration is optional;
the browser image editor and video studio work without the Windows app.

Captures open a Chrome tab with drawing, arrows, shapes, text, opaque redaction,
crop, resize, rotate/flip, regional blur, color adjustments, undo/redo and local
autosave. **Open file** imports images/clipboard images, editable `.winshot.json`
or PNG projects, and a documented subset of native `.winshot` projects.
**Save project** and **Editable PNG** preserve the original and editable annotations. Every image export uses the current edits. Edited PDFs omit
the original text/link layer so covered or cropped source content is not exposed.

**Record & edit video** opens a separate tab with Chrome's screen/window/tab
sharing chooser, local video import, frame-step trimming, crop/rotate/flip, speed
and audio controls. Offline WebCodecs export writes WebM or MP4 using supported
local codecs; compatibility WebM still runs at playback speed. Originals remain
available and no native handoff is involved.
See [browser editing](docs/browser-editing.md) for formats, limits and sharing
editable projects safely.

**Share** offers an attached-image email draft, OneNote clipboard paste, an
offline image note, and prepared file sharing/dragging where the OS supports it.
See [sharing](docs/sharing.md). Per-mode output preferences, selected/current/all-window
batches, ZIP results and an explicitly enabled origin-scoped page API are described
in [batch actions and automation](docs/page-automation.md).

## Build and test

```powershell
npm install
npm run build:extension
npm run test:extension
```

The unpacked package is generated at `browser-extension/dist/winshot-capture`. Automated
extension tests use Puppeteer's supported Chrome-for-Testing extension API and a fresh
temporary profile. Branded Chrome and Edge are used only for isolated fixture/package
smoke tests because current releases block command-line unpacked-extension sideloading.

To run just the actual extension suite (without requiring separately installed
Chrome and Edge), use `npm run test:e2e -- --project=extension-puppeteer`.
`PUPPETEER_EXECUTABLE_PATH` can select an installed Chrome-for-Testing/Chromium
binary when the default Puppeteer browser download is unavailable. Set
`PUPPETEER_SKIP_DOWNLOAD=true` during `npm ci` in that case. The tests also accept
`WINSHOT_EXTENSION_PATH` to verify a specific unpacked build. Every test uses a
disposable profile; never point the harness at your everyday Chrome profile.
The Windows CI runner uses `WINSHOT_TEST_SOFTWARE_GL=1` for a software WebGL
renderer because that VM has no usable GPU context. This is limited to isolated
fixture tests; normal test and live-site runs retain Chrome's default renderer.

For a repeatable check on real websites, run
`npm run test:live -- https://example.com/article https://example.org/another-page`.
It uses the real popup, captures each page, downloads PNG and PDF, checks PNG
dimensions and parses the PDF with an independent reader. Results and screenshots
go to a printed temporary folder. Failed or partial captures return a nonzero exit
code. This is bounded to two minutes, 50,000 CSS pixels and 100 tiles per page;
it uses a fresh profile with no account sessions and honors the environment's proxy.

`npm run test:live:matrix -- <URLs>` tests the real popup's visible, full-page,
dragged-region and exact-element modes on every public URL, saves PNGs and UI
screenshots, parses full-page PDF exports, and checks restoration. Set
`WINSHOT_LIVE_MODES=frame` for pages with a visible public iframe, or
`WINSHOT_LIVE_BATCHES=1` to additionally test all-tabs and URL-list captures in
both visible/full-page mode and an all-tabs PDF using the first three URLs.
The matrix reports partial/failed captures as failures; blocked sites and missing
frames are recorded explicitly, not counted as successful captures.

## Capture reliability and responsiveness

Capture waits for visible images to load and decode within each tile's bounded
settle deadline. Unfinished images or changing layout produce a partial result,
with a warning, rather than a false complete claim. Capture styling and sticky
handling reach open shadow roots, including roots attached to existing hosts while
scrolling. Their new visible images must load too. Axis-aligned scaled scrolling panels use visual
coordinates consistently; scroll position and original CSS are restored afterward.
On fixed-height app layouts, Entire page detects a large central scrolling panel
and captures its hidden content. The result states which area was captured; use
Select an element when a different panel is wanted.

Finite CSS/Web Animations entrance effects finish before capture so scroll-revealed
content is visible. Repeating effects pause, and scroll/view-timeline effects use
a temporary final-keyframe snapshot. Original animation state is restored afterward;
entrance effects created during capture remain finished rather than replaying.
Positioning is rechecked when nodes/classes/styles change, so headers that become
fixed and late-inserted floating footers do not repeat across tiles. Hidden overlays
retain the page's own visibility rules. Pickers start in the top page immediately
and bound setup/cleanup of embedded frames, with per-picker identities protecting
new selections from late messages belonging to an earlier selection. Region drags
use a top-page input plane so they can cross embedded frames without losing mouseup.
Embedded documents that finish navigating while a picker is open receive the picker
in their new document; cancelled picker identities cannot revive old overlays.
Shared tile bands retain already captured fixed-element pixels, so a tall sidebar
or a header on a page only slightly taller than the viewport cannot be cut off by
the next tile. Current-tile floating elements keep their normal paint priority.
JavaScript
canvas rendering and arbitrary scroll handlers cannot all be frozen; changing
overlap pixels or effects that cannot be stabilized produce partial results.

Stop remains visible in the popup, cancels delays as well as active captures, and
new capture controls show a busy state immediately. Button feedback and the capture
indicator respect reduced-motion preferences. Full-resolution tiles stay in local
IndexedDB; seam checking loads only overlapping neighbors and History thumbnails
read just the first tile. Chrome limits screenshots to two calls per second, so
the capture interval deliberately stays above 500 ms.
Concurrent captures in separate windows share one screenshot queue so they cannot
exceed that browser-wide quota.
Progress and countdown controls are scoped to their browser window, while batch
progress remains global. Captures open their editor in the source window. Stop
keeps the same cancellation signal through countdown and asynchronous storage
startup, and countdowns allow same-document History API URL changes.
Popup controls remain disabled with a loading indicator until settings, handlers
and initial capture status are ready. Tests wait for that same visible readiness
state; a slow storage read cannot silently discard the first capture click.
Capture timing uses a monotonic clock, and temporary Chrome quota/readback failures
get one bounded retry. The first scrolling tile must match across two consecutive
browser readbacks. A third read can recover an initially stale compositor frame;
continuously changing initial pixels produce a partial result. This adds one
quota-spaced read on a settled page and records the confirmation in the report.
URL batches subscribe before checking load completion and
recheck tabs after activation so reloading or discarded pages are not captured early.
Batch readiness accepts a parsed top document while offscreen embeds are still
loading; visible images still use the capture engine's bounded readiness checks.
Same-document History API/fragment changes are allowed and reported. Every tile
verifies a per-document identity; real navigation of the top or selected frame
still aborts, while unrelated child-frame loads do not.

The build extracts all toolbar/package icons directly from the Windows app's
`src/WinShot/Assets/winshot.ico`; popup/editor branding and page icons use the same
mark. See [third-party notices](THIRD_PARTY_NOTICES.md#capture-research) for the
open-source capture implementations reviewed during this work.

## Permissions

The package requires site access plus `activeTab`, `scripting`, `downloads`, `storage`, and
`webNavigation`. Site access is needed for reliable all-tab batches and permitted cross-origin
frame capture; WinShot does not use debugger access or send page data to a service.
`nativeMessaging` is optional; `tabs` is required for capture/history workflows.

## Merged product surface (2026-08-07)

The Claude-built v0.1 extension was audited (cross-audit in `.claudehelper/codex-cross-audit.md`),
its engine retired, and its product surface reimplemented here: capture History page with
thumbnails/retention (`src/history.html`), Copy-to-clipboard and Print in the editor, right-click
"repeat last capture" context menu (morphs to Stop), FireShot-style default hotkeys
(Alt+Shift+1/3/4, Ctrl+Shift+S = repeat-or-stop; unset automatically if another extension such as
FireShot already claims them), FireShot-compatible filename tokens with an atomic %n counter, and
toolbar icons. Stitching upgrades from OSS research (ShareX/Snapzy/pic/OpenScreenShot, see
THIRD_PARTY_NOTICES.md): sticky elements are demoted to static instead of hidden (no blank holes,
no duplicated headers), tolerant seam scoring, all-neighbor seam minimums, and reachable-bottom
extent clamping. Restoration is now verified by read-back, captures are window-locked, and every
screenshot re-verifies the active tab.

## Current support boundary

The capture engine supports visible viewport, full page, dragged selection with edge auto-scroll,
Ctrl-square and Alt-full-width selection, exact element selection, self-scrolling/nested panels,
horizontal and vertical tiling, fixed/sticky handling, delayed capture, configurable safety limits,
dynamic extent growth, virtualized content, exact deep-frame selection, visible/full all-tab and
URL-list batches, one-click all-tabs PDF, tile-backed captures beyond 32K, cancellation, watchdog
restoration, and local export. WinShot desktop already supplies browser-window capture. The extension
can request that selector through `winshot://`, but this custom-protocol convenience path does not yet
provide the reliable success/failure acknowledgement that a Native Messaging bridge would provide.

PDF export includes single/multipage layout, standard or custom page sizes, searchable Unicode text,
working website links, and optional headers, footers, and watermarks. DRM video can be blank by
browser policy. Canvas/WebGL/video are captured as rendered pixels and explicitly warned when their
motion can lower seam confidence. See `docs/fireshot-capture-matrix.md` for the FireShot comparison
and `docs/capture-parity.md` for the support boundary.
