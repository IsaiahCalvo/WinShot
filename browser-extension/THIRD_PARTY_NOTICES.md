# Third-party notices

The shipped WinShot Capture extension has no third-party runtime packages. It bundles Lucide icon artwork (see below).

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

## Lucide

The Copy, Print, Open in WinShot, History, Settings, download, details-panel and
fit icons in `src/lucide.js` come from Lucide (https://lucide.dev), lucide-static
0.469.0, under the ISC License:

> ISC License
>
> Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
> Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.
>
> Permission to use, copy, modify, and/or distribute this software for any
> purpose with or without fee is hereby granted, provided that the above
> copyright notice and this permission notice appear in all copies.
>
> THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
> WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
> MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
> ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
> WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
> ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
> OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
