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

## Permissions

The package requires site access plus `activeTab`, `scripting`, `downloads`, `storage`, and
`webNavigation`. Site access is needed for reliable all-tab batches and permitted cross-origin
frame capture; WinShot does not use debugger access or send page data to a service. `tabs` and
`nativeMessaging` remain optional.

## Current support boundary

The capture engine supports visible viewport, full page, dragged selection with edge auto-scroll,
Ctrl-square and Alt-full-width selection, exact element selection, self-scrolling/nested panels,
horizontal and vertical tiling, fixed/sticky handling, delayed capture, configurable safety limits,
dynamic extent growth, virtualized content, exact deep-frame selection, visible/full all-tab and
URL-list batches, one-click all-tabs PDF, tile-backed captures beyond 32K, cancellation, watchdog
restoration, and local export. Browser-window capture is handed to the existing WinShot desktop
capture command through the `winshot://` protocol.

PDF export includes single/multipage layout, standard or custom page sizes, searchable Unicode text,
working website links, and optional headers, footers, and watermarks. DRM video can be blank by
browser policy. Canvas/WebGL/video are captured as rendered pixels and explicitly warned when their
motion can lower seam confidence. See `docs/fireshot-capture-matrix.md` for the FireShot comparison
and `docs/capture-parity.md` for the support boundary.
