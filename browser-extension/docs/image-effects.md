# Browser image adjustments

The editor keeps the original capture tiles and editable annotations. Size,
quarter-turn rotation, horizontal/vertical flips, brightness, contrast, saturation,
grayscale, sepia and regional blur are stored with the capture and in projects.
Undo/redo includes these adjustments. Cropping resets the output size to the new
crop's natural dimensions while retaining orientation; **Reset size** removes
resizing while retaining the crop and orientation. Rotating swaps output width
and height. Drawing and cropping continue to use the original image coordinates,
with the displayed pointer position mapped through the inverse transform.

All raster exports, PDF, clipboard and print flatten the same edited pixels.
Edited PDFs omit the original selectable text and links. Native Canvas and SVG
filters provide the adjustment preview and output; no external image service is
used. JPEG/PDF have a white background; PNG/WebP retain transparency.

Blur is a visual effect, **not secure redaction**. Its radius is 1–40 pixels in the
output image. Opaque Redact annotations always render above blur regions. Use
Redact for private information, then share a normal image or PDF. Editable
projects and editable PNG files include the original source pixels and must not
be used to distribute permanently redacted copies.

## Bounds and rendering

Edited output is limited to 100 million pixels and 200,000 pixels per edge.
Exports render in tiles of at most 2048 × 1024 pixels. Blur uses up to 120 pixels
of neighboring context on each side, keeping the temporary padded raster at
most 2288 × 1264 pixels rather than allocating a full-page canvas. Source tiles
are decoded sequentially and closed after drawing. When resizing, native source
patches include neighboring pixels from abutting tiles before the patch is
resampled; independently resizing each tile would clamp its edges and create
colored seams. These source patches are at most 2048 × 1024 pixels. Larger output
interiors are partitioned at whole output-pixel boundaries, while source aprons
keep the interpolation kernel away from artificial patch edges. Integer crops,
quarter-turns and flips at the original resolution use a direct drawing fast path.
Color adjustments apply after source tiles are composed, so transparent overlaps
in imported projects match between the grouped preview and flattened exports.
Only overlapping tiles with nonneutral adjustments require composited patches
at the original resolution; ordinary nonoverlapping or neutral exports keep the
direct path. The tall-preview check samples native browser paint at both ends of
a 19,000-pixel image, alongside actual exported-pixel checks.

For severe downscaling, only axes whose remaining scale is below 1/16 receive
progressive reductions of up to eightfold before the final resampling pass.
These passes also use bounded patches, preserve alpha, and apply the image
adjustments after resampling. A multi-pass severe reduction is not guaranteed to
be pixel-identical to one native Canvas resize of a monolithic source. Ordinary
resizing is checked against that independent native Canvas oracle, including
hard edges, gradients and fractional tile boundaries.

Blur halos avoid artificial boundaries between export tiles. Quarter-turn transforms work for captures taller
than the browser's single-canvas limit. JPEG/WebP still have their existing
single-canvas format limits; PNG/PDF support tile-backed captures.

Brightness/filter conversion uses the browser's 8-bit raster pipeline, so a
half-intensity channel may round to 127 or 128. These are browser editing tools,
not a color-managed professional photography workflow.

## Open-source references reviewed

- [Konva](https://github.com/konvajs/konva/blob/master/LICENSE), MIT; reviewed its
  [Blur implementation](https://github.com/konvajs/konva/blob/master/src/filters/Blur.ts)
  and its attention to premultiplied alpha. WinShot delegates filtering to native
  browser Canvas/SVG filters instead of including StackBlur or the Konva runtime.
- [Pica](https://github.com/nodeca/pica/blob/master/LICENSE), MIT;
  [documentation](https://github.com/nodeca/pica/blob/master/README.md) describes
  bounded tile processing, sequential work and image decoding independently from
  resizing. WinShot keeps its existing tiled capture architecture and requests
  high-quality native Canvas image smoothing.
- [Fabric.js license](https://github.com/fabricjs/fabric.js/blob/master/LICENSE),
  MIT, reviewed as an alternative object/canvas editor framework. Its runtime is
  not included; existing WinShot vector annotations remain editable.

No code from these libraries was copied into the extension, and no new runtime
library dependency was added for image effects.
