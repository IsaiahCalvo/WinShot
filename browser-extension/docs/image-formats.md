# Image formats and portable edits

Save PNG, JPEG, WebP, GIF and BMP create flattened images with the current
crop, transformations, effects and annotations applied. They contain no
WinShot project metadata or original image tiles. GIF is a still frame,
uses up to 256 palette colors and is limited to 16 million pixels. GIF/BMP
use a white background for transparent areas; BMP is24-bit RGB and limited
to 100 MB. PNG/PDF remain the choices for very large captures.

Editable PNG is a separate, explicit export. Any regular PNG viewer sees
the flattened edited image. WinShot recognizes a private ancillary `wsPJ`
chunk and restores original tiles and editable annotations. This chunk is
unsafe-to-copy under the PNG specification: another image editor may remove
it, leaving a regular flattened image. Reopen in WinShot through Open image.

**Editable PNG and .winshot.json contain the original image.** Redactions
and crops in an editable file can be undone. Share ordinary PNG/JPEG/WebP/
GIF/BMP/PDF when the original must not be included. The editable export
button and completion message call out this distinction. Clipboard, print
and browser sharing continue to use ordinary flattened pixels.

## Portable project schema

The `wsPJ` chunk contains UTF-8 JSON with `format:winshot-browser-project`
and `version:1`, using the same schema as .winshot.json. The file includes
original source width/height, base64 PNG tiles and a version1 editorDocument
with crop and annotations in original pixel coordinates. Optional validated
transform fields are rotation0/90/180/270, boolean flipX/flipY and bounded
output width/height; effects are brightness/contrast/saturation0..200 and
grayscale/sepia0..100. Blur annotations carry output-pixel radius1..40.
Unknown fields are discarded; executable filters/markup are not imported.
Undo history remains session-local. The schema is a WinShot browser project,
separate from the restricted native Windows-project importer described below.

Imports are limited to 100 MB and100 million source/output pixels, with
200,000-pixel edges,1,000 tiles and10,000 annotations. PNG chunk lengths,
CRC32 checksums, header/end structure and duplicate editable metadata are
validated before storage. Embedded project validation and raster decoding
complete before the atomic history write. Ordinary PNG output is freshly
encoded from edited pixels, so importing an editable file does not cause
its hidden originals to propagate into normal exports.

GIF/BMP decoding uses Chrome; animated GIFs import their first frame. GIF
export uses pinned gifenc1.0.3 under MIT, vendored without runtime network
requests. The full license and artifact provenance ship in src/vendor/.

## Native Windows .winshot import

WinShot desktop writes a ZIP with source.png and annotations.json schema1.
The browser accepts the unannotated source and an explicit editable subset:
unfilled, unrotated rectangles/ellipses; opaque plain freehand paths; straight
open-V/Thin arrows; and opaque BlackOut redactions. Translation and layer order
are retained. Native black redactions remain exactly #000000; older browser
redactions retain their #111111 default. Browser arrowhead rendering is called
out in the imported capture warning.

Unsupported native annotations or styles reject the whole file before History
is changed. This includes embedded image annotations, closed/curved arrowheads,
rotations, filled/translucent shapes, native text/callout/spotlight/magnifier
objects and non-blackout redaction effects. No private annotation or redaction
is silently dropped. This is not FireShot's proprietary project format.

ZIP paths are exactly source.png and annotations.json, with no duplicates,
overlapping entries, encrypted entries, ZIP64 or disk-spanning archives.
Both stored and deflated entries are supported. Local/central headers and
data descriptors must agree; expanded lengths and CRC32 are checked.
Streaming fflate Inflate receives8KB compressed slices and rejects output
beyond declared size. Total expanded data is capped at100MB, annotation JSON
at8MB. All project-schema and actual PNG decoding checks complete before
the atomic import. fflate0.8.2 is vendored under MIT with verified provenance.
