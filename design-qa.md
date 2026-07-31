# Quick Access overlay design QA

## Source visual truth

- User attachment: `C:\Users\icalvo\.codex\attachments\64134e48-32f7-4468-bfc9-53ccfec2bd44\download-window-svgrepo-com.svg`
- Embedded unchanged source: `src/WinShot/Assets/download-window-svgrepo-com.svg`
- Source pixels: SVG declares 800 x 800 with a 32 x 32 viewBox.
- Focused source render: `docs/evidence/quick-access-overlay/20260731/after-user-layout/save-icon-source-render.png` at 64 x 64 pixels.

## Implementation evidence

- Full idle state: `docs/evidence/quick-access-overlay/20260731/after-user-layout/idle.png`
- Full hover state: `docs/evidence/quick-access-overlay/20260731/after-user-layout/hover.png`
- Viewport: 190 x 120 logical pixels at the harness's 96-DPI layout.
- State: dark hover scrim with all actions visible.
- Implemented Save asset: approximately 14 x 14 pixels inside a 22 x 22 corner button at 100% DPI; it scales with the button at higher DPI.
- Density normalization: none required. The vector path is rendered independently at the source-comparison and consuming sizes.

## Target layout

- Pin top-left; Close top-right.
- Annotate/Edit bottom-left; Save bottom-right.
- One Copy pill centered in the preview.

## Comparison evidence

- Full-view comparison: the final 190 x 120 hover render was opened with the 64 x 64 source-icon render in the same comparison input.
- Focused comparison: required because the Save detail is too small to judge from the full overlay alone. The focused render preserves the source's browser-window outline, title-bar marks, vertical stem, and downward arrow.
- The source SVG path is embedded unchanged and rasterized by the app; the Save control does not substitute a text glyph when the asset loads.

## Required fidelity surfaces

- Fonts and typography: the centered Copy label retains the existing Segoe UI treatment; the supplied Save artwork contains no text.
- Spacing and layout rhythm: all four 22 x 22 corner buttons use equal 8-pixel insets; the 58 x 29 Copy pill is centered on both axes.
- Colors and tokens: Save uses the same dark foreground and cream circular surface as the other actions; contrast remains consistent.
- Image quality and asset fidelity: the exact supplied vector path is used, with transparent antialiased rendering and no raster stretching.
- Copy and content: accessible name and tooltip are `Save` and `Save (Ctrl+S)`; visible Copy text is unchanged.

## Comparison history

- Earlier P1: Save was a second center pill and Annotate was centered at the bottom, which conflicted with the user's explicit layout.
- Fix: moved Annotate to bottom-left, added Save to bottom-right using the supplied SVG, and vertically centered Copy as the only pill.
- Post-fix evidence: `docs/evidence/quick-access-overlay/20260731/after-user-layout/hover.png`.

## Findings

- P0: none.
- P1: none.
- P2: none.
- P3: live candidate timing, high contrast, and physical mixed-DPI placement remain runtime-verification items; automated geometry, SVG-loading, keyboard, and accessibility checks pass.

final result: passed
