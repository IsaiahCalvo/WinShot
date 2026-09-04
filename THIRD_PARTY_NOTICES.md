# Third-party notices

Every package WinShot ships, and where it came from. Verified 2026-09-04 against the
resolved package graph in `src/WinShot/obj/project.assets.json` and each package's
own `.nuspec` in the local NuGet cache — not from memory or from a badge. Bundled
image assets are covered separately under "Icon assets".

Fourteen packages reach the resolved package graph: thirteen MIT, one Apache-2.0.
One of the fourteen (`SkiaSharp.NativeAssets.macOS`) is in the graph but is not
carried into the win-x64 output; it is listed anyway, because listing a package that
turns out not to ship is the harmless mistake. Nothing in the graph is copyleft. Test-only packages (xunit, Newtonsoft.Json,
`Microsoft.TestPlatform.*`) are not distributed and carry no notice obligation, so
they are deliberately absent from the count and from the list below.

MIT and Apache-2.0 both require their notices to accompany the **distributed
binary**, not merely to sit in the source repo. See "Still open" at the bottom.

## Studied, not copied

### ShareX — https://github.com/ShareX/ShareX — GPL-3.0

Behaviour reference only. **No ShareX source is copied, linked, or included in
WinShot**, and WinShot links no GPL code.

WinShot's region selector and its scrolling capture were written after studying how
ShareX behaves. Two files carry that note in their own headers:

- `src/WinShot/Capture/RectangleTween.cs` — the hover highlight's ~200 ms glide,
  and the rule that retargeting mid-flight resumes from where the rectangle
  currently is rather than from the old target. WinShot's tween is its own code:
  a single class, stopwatch-timed, pixel-snapped output, exact landing on the
  target.
- `src/WinShot/Core/WindowEnumerator.cs` — the window / client-area / child-control
  snap model: deepest-first ordering, cloaked (UWP ghost) windows filtered out,
  non-activatable and click-through windows skipped, DWM extended frame bounds
  rather than `GetWindowRect`. These follow from the Win32 window model; the
  enumeration itself, its P/Invoke layer, its time budget and its coverage pass
  are WinShot's.

Three other files name ShareX in a comment — `Capture/FastRegionSelectorDialog.cs`,
`Scrolling/ImageStitcher.cs` and `Scrolling/ScrollingCaptureService.cs` — but only
to say what ShareX is observed to do (always-on element detection, longest-run row
matching when stitching, re-foregrounding the window before a scroll key). Those are
observations about behaviour, not ported code. `Core/HotkeyConflictInspector.cs`
also names ShareX, as one of the apps whose hotkeys can clash with WinShot's; that
is a data row, not a citation.

The NVIDIA GeForce overlay's window class name (`CEF-OSC-WIDGET`), noted in
`WindowEnumerator.cs`, is a fact about NVIDIA's software: the overlay enumerates as
an ordinary top-level window. Every screenshot tool that walks the window list hits
it.

### Microsoft PowerToys — https://github.com/microsoft/PowerToys — MIT — **PENDING, not yet used**

`docs/research/2026-09-02-cleanshot-video-sweep.md` plans a port of PowerToys'
`MeasureToolCore` edge detection into a future `src/WinShot/Capture/EdgeSnap.cs`.
Nothing is ported yet. **When that port lands**, MIT requires the copyright notice
and the full permission notice to travel with all copies: the verbatim MIT licence
text plus "Copyright (c) Microsoft Corporation" must be added to this file, must
ship with the binary, and an attribution header must sit in `EdgeSnap.cs` itself.
Until then this entry is a reminder, not a notice.

## Shipped packages

### MIT

Copyright cells are the verbatim `<copyright>` string from each package's own
`.nuspec`, because that is the notice MIT requires to travel with the binary.

| Package | Version | Copyright | Source |
|---|---|---|---|
| ScreenRecorderLib | 6.6.0 | Copyright © Sverre Kristoffer Skodje 2025 | https://github.com/sskodje/ScreenRecorderLib |
| SkiaSharp | 3.119.4 | © Microsoft Corporation. All rights reserved. | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | © Microsoft Corporation. All rights reserved. | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | © Microsoft Corporation. All rights reserved. | in the graph, not shipped on win-x64 |
| System.Text.Json | 9.0.1 | © Microsoft Corporation. All rights reserved. | https://github.com/dotnet/runtime |
| System.Text.Encodings.Web | 9.0.1 | © Microsoft Corporation. All rights reserved. | https://github.com/dotnet/runtime |
| System.IO.Pipelines | 9.0.1 | © Microsoft Corporation. All rights reserved. | https://github.com/dotnet/runtime |
| Vortice.Direct3D11 | 3.8.3 | Copyright (c) Amer Koleci and Contributors | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.DXGI | 3.8.3 | Copyright (c) Amer Koleci and Contributors | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.DirectX | 3.8.3 | Copyright (c) Amer Koleci and Contributors | https://github.com/amerkoleci/Vortice.Windows |
| Vortice.Mathematics | 2.1.0 | Copyright (c) Amer Koleci and Contributors | https://github.com/amerkoleci/Vortice.Mathematics |
| SharpGen.Runtime | 2.4.2-beta | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci | https://github.com/SharpGenTools/SharpGenTools |
| SharpGen.Runtime.COM | 2.4.2-beta | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci | https://github.com/SharpGenTools/SharpGenTools |

The two `SharpGen.Runtime` packages resolve to a **prerelease** (`2.4.2-beta`) in
the shipping build. That is a supply-chain note, not a licensing one.

The MIT permission notice, reproduced once for all of the above:

> Permission is hereby granted, free of charge, to any person obtaining a copy of
> this software and associated documentation files (the "Software"), to deal in
> the Software without restriction, including without limitation the rights to
> use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
> the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
> FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
> IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### Apache-2.0

**ZXing.Net 0.16.11** — https://github.com/micjahn/ZXing.Net — used for QR/barcode
decoding. Apache-2.0 asks for more than MIT does: the full licence text must be
included with the distribution, any upstream `NOTICE` file must be propagated, and
modified files must be marked as changed. WinShot does not modify ZXing.Net.

The package itself ships **no licence file** — only the SPDX expression in its
`.nuspec` — so the Apache-2.0 text has to come from upstream
(https://www.apache.org/licenses/LICENSE-2.0.txt) rather than out of the package.

### Behind the MIT wrapper

`SkiaSharp` is an MIT wrapper, but the native `libSkiaSharp` binary it ships
embeds Google's **Skia**, which is **BSD-3-Clause**, along with Skia's own
third-party dependencies. Do not let the wrapper's MIT stand in for them: the
authoritative list is shipped inside the package as
`skiasharp.nativeassets.win32/3.119.4/THIRD-PARTY-NOTICES.txt`, and that file
should be reproduced verbatim with the distribution.

## Icon assets

Sixteen files sit under `src/WinShot/Assets/` and all sixteen ship inside the binary:
fifteen SVGs as `EmbeddedResource`, plus `winshot.ico` as the `ApplicationIcon`.

Fifteen of them — `winshot.ico` and the fourteen `quick-access-*.svg` — are WinShot's
own: one icon family, viewBox 0 0 24 24, `currentColor`, stroke 1.85, drawn for this
app and carrying no upstream marker.

One is not. `download-window-svgrepo-com.svg` came from **SVG Repo**
(https://www.svgrepo.com) — its own header comment says so, and unlike the others it
is 800×800 with a hard-coded `#000000` fill. SVG Repo licences per icon, anywhere
from CC0 to CC-BY with attribution required, so this file's terms are **unverified**.
Nothing in the code references it; only `WinShot.csproj` still embeds it. T7 deletes
it, which resolves this entry — if it is ever kept instead, its individual SVG Repo
licence has to be looked up and recorded here.

## Related

The browser extension has its own notices, covering different code:
[browser-extension/THIRD_PARTY_NOTICES.md](browser-extension/THIRD_PARTY_NOTICES.md).

## Still open

- **WinShot itself has no LICENSE file.** With none, the default is "all rights
  reserved" — nobody may copy, modify or redistribute it. That is a legitimate
  choice for a personal, closed app, but right now it is an accident rather than a
  decision. Isaiah's call.
- **These notices must ship, not just live here.** MIT and Apache-2.0 obligations
  attach to the distributed binary, so this file (plus the Apache-2.0 text and
  Skia's `THIRD-PARTY-NOTICES.txt`) needs to be carried by the installer and
  reachable from the About pane. Not wired yet. Tracked in the master plan under
  T7's About-pane row so it does not go with this branch.
