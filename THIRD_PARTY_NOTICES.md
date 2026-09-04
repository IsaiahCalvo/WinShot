# Third-party notices

WinShot ships no third-party source code. This file records the projects it depends on at
runtime, and the projects whose *behaviour* it was designed against without copying code.

## Behaviour references — no code copied

### ShareX (GPL-3.0)

<https://github.com/ShareX/ShareX>

WinShot is not a derivative work of ShareX. No ShareX source, in whole or in part, was
copied, translated, or adapted into this repository. Two WinShot files were written to match
*observed ShareX behaviour* — what its region selector does, not how it does it — and say so
in their headers:

- `src/WinShot/Capture/RectangleTween.cs` — a hover highlight that glides to a new rectangle
  instead of teleporting, and that re-origins from its current on-screen position when
  retargeted mid-flight. WinShot's implementation is a stopwatch-driven lerp with its own
  API (`Retarget` / `Update` / `Stop` / `IsActive`); ShareX's equivalent is a different type with a
  different shape.
- `src/WinShot/Core/WindowEnumerator.cs` — which on-screen rectangles a window picker should
  offer, and in what order: child controls before their parent, client area before frame,
  z-order front to back, containment-deduped. WinShot's implementation is its own
  `EnumWindows` walk feeding a `SnapRect` list, and it layers MSAA/UIA and pixel tiers that
  ShareX does not have.

Because no ShareX code is present, the GPL-3.0 does not extend to WinShot.

### Microsoft PowerToys (MIT)

<https://github.com/microsoft/PowerToys>

Reserved. The planned measurement mode (`SelectorMode.Measure`) ports PowerToys' Screen Ruler
edge-detection algorithm (`EdgeDetection.cpp`). That port is not in the tree yet; when it
lands, `src/WinShot/Capture/EdgeSnap.cs` carries the MIT notice and this section is updated to
name it. MIT permits the port with attribution.

## Runtime dependencies

| Package | Version | Licence |
|---|---|---|
| [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) | 6.6.0 | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 3.119.4 | MIT |
| [System.Text.Json](https://github.com/dotnet/runtime) | 9.0.1 | MIT |
| [Vortice.Direct3D11](https://github.com/amerkoleci/Vortice.Windows) | 3.8.3 | MIT |
| [Vortice.DXGI](https://github.com/amerkoleci/Vortice.Windows) | 3.8.3 | MIT |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | 0.16.11 | Apache-2.0 |

## Build- and test-only dependencies

Not redistributed with WinShot.

| Package | Version | Licence |
|---|---|---|
| [xunit](https://github.com/xunit/xunit) | 2.9.3 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | 3.1.5 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 18.6.0 | MIT |

## Fonts and icons

WinShot draws its glyphs from **Segoe Fluent Icons** / **Segoe MDL2 Assets**, which ship with
Windows and are licensed for use on Windows under the Microsoft software licence terms for the
operating system. No font file is redistributed.
