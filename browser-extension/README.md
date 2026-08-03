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

The public package requires only `activeTab`, `scripting`, `downloads`, and `storage`.
It does not require `<all_urls>` or debugger access. `tabs` and `nativeMessaging` are
optional permissions reserved for explicit batch capture and desktop handoff.

## Current support boundary

The working vertical slice supports visible viewport, full page, dragged selection with
edge auto-scroll, exact element selection, self-scrolling/nested panels, horizontal and
vertical tiling, fixed/sticky suppression, dynamic extent growth, virtualized content,
tile-backed captures beyond 32K, cancellation, watchdog restoration, and local export.

Cross-origin frame pixels are captured when visible, but hidden cross-origin frame content
cannot be inspected or scrolled without extra site access. DRM video can be blank by browser
policy. Canvas/WebGL/video are captured as rendered pixels and explicitly warned when their
motion can lower seam confidence. See `docs/capture-parity.md` for remaining gaps.
