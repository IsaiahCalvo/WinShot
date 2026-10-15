# WinShot Capture browser extension

WinShot Capture is a local-first Manifest V3 extension. It captures real browser pixels,
stores large captures as tiles, validates coverage and overlap confidence, and exports
PNG, JPEG, WebP, or PDF without a cloud service. WinShot desktop integration is optional;
the standalone editor/export page works without the Windows app.

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

## Capture reliability and responsiveness

Capture waits for visible images to load and decode within each tile's bounded
settle deadline. Unfinished images or changing layout produce a partial result,
with a warning, rather than a false complete claim. Capture styling and sticky
handling reach open shadow roots. Axis-aligned scaled scrolling panels use visual
coordinates consistently; scroll position and original CSS are restored afterward.
On fixed-height app layouts, Entire page detects a large central scrolling panel
and captures its hidden content. The result states which area was captured; use
Select an element when a different panel is wanted.

Stop remains visible in the popup, cancels delays as well as active captures, and
new capture controls show a busy state immediately. Button feedback and the capture
indicator respect reduced-motion preferences. Full-resolution tiles stay in local
IndexedDB; seam checking loads only overlapping neighbors and History thumbnails
read just the first tile. Chrome limits screenshots to two calls per second, so
the capture interval deliberately stays above 500 ms.
Concurrent captures in separate windows share one screenshot queue so they cannot
exceed that browser-wide quota.

The build extracts all toolbar/package icons directly from the Windows app's
`src/WinShot/Assets/winshot.ico`; popup/editor branding and page icons use the same
mark. See [third-party notices](THIRD_PARTY_NOTICES.md#capture-research) for the
open-source capture implementations reviewed during this work.

## Permissions

The package requires site access plus `activeTab`, `scripting`, `downloads`, `storage`, and
`webNavigation`. Site access is needed for reliable all-tab batches and permitted cross-origin
frame capture; WinShot does not use debugger access or send page data to a service. `tabs` and
`nativeMessaging` remain optional.

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
