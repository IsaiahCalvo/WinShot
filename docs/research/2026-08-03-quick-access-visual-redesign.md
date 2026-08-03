# Quick Access HTML design adoption - 2026-08-03

## Scope

Polish only the post-capture Quick Access overlay. Preserve its five actions,
keyboard shortcuts, accessibility names, placement, auto-close behavior, and
local-only implementation.

## Selected source

- The user-selected `windows_screenshot_hover_cards_custom_pin.html` artifact is
  the visual source for this revision.
- Idle shows only the unobstructed capture thumbnail. Hover blurs and darkens the
  thumbnail, then reveals translucent controls above it.
- The exact SVG paths from the selected design are embedded locally for Pin,
  Close, Edit, Save, and Copy. No network or cloud dependency was added.
- The selected pill treatment, Segoe UI label, subtle borders, shadows, and
  light/dark palettes are carried into the native overlay.
- The user's previously approved placement remains Pin top-left, Close
  top-right, Edit bottom-left, Save bottom-right, and Copy centered.

## Theme behavior

- WinShot remains dark-only today, so the installed candidate uses the dark
  palette.
- A light palette is implemented and rendered under test so the overlay is
  ready to follow a future app-wide theme setting without another redesign.
- Theme selection stays local and adds no timer, background process, or service.

## HTML proportion and tooltip refinement

- The centered Copy control now uses the HTML card's proportions: a 78 by 27
  logical-pixel pill with a 7-point Segoe UI label and a smaller line icon.
- The native Windows tooltip was removed. Button hover now uses a compact,
  rounded, theme-aware label (`Pin`, `Close`, `Edit`, `Save`, or `Copy`) in a
  non-activating tool window, so it cannot take keyboard focus.
- Tooltip measurement, placement, dark/light drawing, and the complete overlay
  are rendered and inspected through the in-process bitmap harness. These checks
  do not move the pointer, send keys, activate a window, or use the visible desktop.
- The refinement adds 22,244 bytes across the DLL and symbols compared with the
  installed HTML-design candidate; the published file and dependency sets are unchanged.

## Verification

- Quick Access focused tests: 38/38 passed.
- Full Release suite: 502/502 passed.
- Release x64 self-contained publish succeeded with product version 1.2.1.
- Common published files grew by 21,824 bytes (20 KB in `WinShot.dll` and
  1,344 bytes in symbols); the file and dependency sets are unchanged.
- Render evidence includes idle, hover, Save hover, and Copy hover in both
  palettes.
