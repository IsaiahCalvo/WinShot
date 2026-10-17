# Third-party notices

The extension bundles the locally vendored GIF encoder, video conversion library
and MIME email composer listed below. No runtime code is loaded from a CDN.

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

### Snapzy license

BSD 3-Clause License

Copyright (c) 2026, Trong Duong Duc

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Capture research

The October 2026 reliability work reviewed these public implementations. No
additional upstream source was copied and no runtime dependencies were added.

| Project | License | Useful technique and WinShot decision |
| --- | --- | --- |
| [Snapzy scrolling capture](https://github.com/duongductrong/Snapzy/blob/master/docs/SCROLLING_CAPTURE.md) | BSD-3-Clause | Bounded raster history and separate preview/commit work. WinShot keeps tile history in IndexedDB and loads only overlapping neighbors for seam checks; thumbnails read one tile. Native macOS capture code is not a browser-extension implementation. |
| [pic](https://github.com/kidandcat/pic/blob/main/main.go) | MIT | Scroll positions, fixed-edge handling, and stitching. Keep WinShot's measured placement and dynamic extent checks rather than use a fixed 300 ms settle alone. |
| [GoFullPage's original extension](https://github.com/mrcoles/full-page-screen-capture-chrome-extension/blob/master/page.js) | MIT | Overlapping tiles, zoom rounding, and watchdog cleanup. Keep these existing safeguards; add bounded image readiness instead of trusting a fixed delay. |
| [Playwright screenshotter](https://github.com/microsoft/playwright/blob/main/packages/playwright-core/src/server/screenshotter.ts) | Apache-2.0 | Screenshot styling must reach open shadow roots and be restored. WinShot applies its own capture stylesheet and sticky handling in those roots. |
| [ShareX scrolling capture](https://github.com/ShareX/ShareX/blob/master/ShareX.ScreenCaptureLib/ScrollingCaptureManager.cs) | GPL-3.0 | Reviewed for behavior: adjustable waits, bounded capture, and partial results. No ShareX source copied. |

## Development-only animation fixture

The test corpus uses the unmodified GSAP 3.15.0 distribution under the GSAP
Standard no-charge license. See `tests/vendor/README.md` in the source repository
for its pinned upstream commit. It is not bundled in this extension. Runtime
animation stabilization calls the GSAP installation already present on a page.

## GIF export: gifenc1.0.3

The extension includes the unmodified ES-module distribution of
[gifenc1.0.3](https://github.com/mattdesl/gifenc), by Matt DesLauriers, under
the MIT License. Source, pinned artifact hash and upstream package integrity
are recorded in `src/vendor/gifenc-provenance.json`; the full MIT license is
in `src/vendor/gifenc-LICENSE.md`. It provides palette quantization and GIF
LZW encoding. WinShot exports one still frame with up to256 colors and a
white matte. GIF/BMP decoding uses Chrome's image decoder.


## Mediabunny 1.61.6

Browser video export uses the unmodified local ESM bundle from
[Mediabunny](https://github.com/Vanilagy/mediabunny), MPL-2.0. The bundle and
license are in `src/vendor/mediabunny/`; its corresponding source archive is
included at `docs/source/mediabunny-1.61.6.tgz`. No third-party code is loaded
from a CDN. Provenance and archive SHA256 are recorded alongside the bundle.
MPL-covered files remain under MPL-2.0; this does not change the license of
WinShot's separate files.

## MIMEText 3.0.28

Email drafts use [MIMEText](https://github.com/muratgozel/MIMEText), MIT.
The standalone browser bundle is vendored at `src/vendor/mimetext/mimetext.mjs`
with only an appended ESM export. Its license, bundled dependency license texts
and package provenance are in that directory. WinShot configures an unsent draft
without a sender so the recipient's mail application can select its sending account.
No message is sent by the extension.

## Sharing references

The OneNote paste path follows the standard multi-format image/HTML/text
clipboard approach documented by Google's web.dev and
[clipboard-polyfill](https://github.com/lgarron/clipboard-polyfill) (MIT).
It uses native Chrome APIs; no polyfill code was copied. File sharing uses
the Web Share API with a prepared File and a separate user gesture.

## Native project ZIP decoding: fflate0.8.2

The extension includes the unmodified browser ES module from
[fflate0.8.2](https://github.com/101arrowz/fflate), by Arjun Barrett, under
the MIT License. Full license and verified pinned npm artifact provenance
are in `src/vendor/fflate-LICENSE.md` and `src/vendor/fflate-provenance.json`.
WinShot feeds bounded compressed slices into streaming Inflate and checks
central/local headers, expanded sizes and CRC32 before importing any record.
