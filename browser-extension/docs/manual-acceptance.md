# Manual acceptance checklist

Automated tests use disposable browser profiles. They cover every browser-native capture mode, but they intentionally do not control the installed WinShot app or the user's personal browser profile.

## Load the extension

1. Open `chrome://extensions`.
2. Turn on **Developer mode**.
3. Choose **Load unpacked**.
4. Run `npm run build:extension` in `C:\Users\icalvo\winshot`, then select `C:\Users\icalvo\winshot\browser-extension\dist\winshot-capture`.
5. Pin **WinShot Capture** to the toolbar.

## Short browser acceptance

1. **Entire page:** capture a long article and confirm its top and bottom are present once.
2. **Visible viewport:** scroll halfway down a page, capture visible, and confirm only the current viewport is included.
3. **Selection:** drag beyond the bottom edge and let it auto-scroll. Also try Ctrl for a square and Alt for full-page width.
4. **Element:** choose **Select an element**, hold Ctrl, and click a normal page item. The output must contain that item, not the whole page.
5. **Scrolling element:** select a panel with its own scrollbar. A yellow outline means hidden content; the output must include the panel's off-screen bottom.
6. **Frame:** choose **Capture a scrolling frame** and click inside an iframe.
7. **Batch:** try all tabs, a URL list, and all tabs to one PDF.
8. **PDF:** search for page text, click a captured link, and try single-page, multipage, Letter/A4, header, footer, and watermark settings.
9. **Editor:** draw with each tool group (Draw, Shapes, Text, Effects, Image), change colours, undo/redo, crop, resize, then Save PNG and PDF and confirm the marks are in both. Reopen the capture from History: the marks are still editable.
10. **After-capture action:** in Settings choose "Save the image" and a Downloads sub-folder, capture, and confirm the file lands there without the editor staying open.
11. **Open in WinShot:** with WinShot desktop running, press Open in WinShot in the editor and confirm the desktop editor opens the saved PNG.

## Desktop-only acceptance

The extension's **Browser window** button opens `winshot://capture-window`. Chrome may ask permission to open WinShot. Accept it, select the Chrome window, and confirm the result enters normal WinShot history/post-capture handling.

This last handoff is not fully automated and has no success acknowledgement. A Chrome Native Messaging bridge is the recommended production replacement for the custom URL.
