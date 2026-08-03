# Quick Access visual redesign — 2026-08-03

## Scope

Polish only the post-capture Quick Access overlay. Preserve its five actions,
keyboard shortcuts, accessibility names, placement, auto-close behavior, and
local-only implementation.

## Visual correction

- Replaced the cramped 22-pixel bright circles with 28-pixel dark translucent
  rounded controls and consistent Fluent glyphs.
- Replaced the centered white `Copy` label pill with a compact blue icon action.
- Reduced the hover scrim so the capture remains recognizable.
- Kept Pin, Close, Annotate, and Save in their existing corners.
- Added rendered hover evidence for Save and Copy so icon contrast is checked.

## Verification

- Quick Access focused tests: 29/29 passed.
- Full Release suite: 493/493 passed.
- Release x64 self-contained publish succeeded with product version 1.2.1.
- Published package changed by less than 1 KB and added no dependency or idle work.
