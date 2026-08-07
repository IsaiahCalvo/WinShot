# Third-party notices

The shipped WinShot Capture extension has no third-party runtime code or runtime packages.

Development and automated verification use:

- Playwright (`@playwright/test`), Apache License 2.0.
- Puppeteer, Apache License 2.0.

Those tools are development-only and are not copied into the extension package.

## Snapzy

The tolerant seam color-difference metric in `src/background.js` is adapted from
Snapzy (https://github.com/duongductrong/Snapzy), © Trong Duong Duc,
licensed under the BSD 3-Clause License. The sticky-element demotion and
last-tile bottom-band composition approach follows techniques from
kidandcat/pic (https://github.com/kidandcat/pic, MIT) and
OpenScreenShot (MIT). ShareX (GPL v3) was reviewed for behavior only; no
ShareX code is included.
