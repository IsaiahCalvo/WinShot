# WinShot Capture browser extension

WinShot Capture is a local-first Manifest V3 extension. It captures real browser pixels,
stores large captures as tiles, validates coverage and overlap confidence, lets you mark the
capture up with WinShot desktop's editor tools, and exports PNG, JPEG, WebP, or PDF without a
cloud service. WinShot desktop integration is optional; the editor works without the Windows app.

## Editor (2026-11)

The editor uses WinShot desktop's tool groups and its icons (generated from
`src/WinShot/Theme/SurveyToolIcons.xaml` by `tools/port-desktop-icons.py`): Pan and Select;
Draw (pen, highlighter, eraser); Shapes (rectangle, ellipse, line, arrow, numbered step);
Text (plain, bold, outlined, label); Effects (pixelate / blur / black-out redaction, spotlight);
Image (crop, resize). Colours, widths, fills, dashes, undo/redo, keyboard shortcuts (the desktop's
letters: V, P, H, E, R, O, L, A, C, T, X, K) and zoom/pan all work on captures of any height,
because the canvas draws only the visible window. Marks are stored in image pixels on the capture
record (`record.edits`) and are flattened into fresh tiles by `src/annotations.js` before every
export, so PNG/JPEG/WebP/PDF/copy/print all include them and PDF text and links follow the crop.

Other FireShot-parity additions: a Settings page (after-capture action — editor, save image,
save PDF, or copy — Downloads sub-folder, Save As prompt, file names, shortcuts), a full
right-click menu, opening or pasting any image from History, and **Open in WinShot**, which saves
the PNG and launches `winshot://edit:<path>` so the desktop editor opens it.

Store releases are headless — see [docs/chrome-web-store.md](docs/chrome-web-store.md).

## Build and test

```powershell
npm install
npm run build:extension
npm run test:extension
npm run package:extension   # store zip in browser-extension/dist/
```

On Linux as root (containers), the end-to-end launcher adds `--no-sandbox` automatically.

The unpacked package is generated at `browser-extension/dist/winshot-capture`. Automated
extension tests use Puppeteer's supported Chrome-for-Testing extension API and a fresh
temporary profile. Branded Chrome and Edge are used only for isolated fixture/package
smoke tests because current releases block command-line unpacked-extension sideloading.

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
