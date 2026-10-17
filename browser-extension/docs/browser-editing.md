# Edit locally in a Chrome tab

WinShot Capture does not need the Windows application to edit a screenshot or
recording. Page captures open the image editor automatically. Use **Open image
or project** in the popup, or **Open file** in the editor, to bring in an image,
paste an image, or reopen an editable browser project.
Each document has its own tab, and edits are saved in the extension's local
history. No file is uploaded.

## Image editor

Use Select, Arrow, Rectangle, Ellipse, Pen, Text, Blur, Redact, and Crop.
Size/orientation controls resize, rotate by quarter turns, and flip the image.
Image adjustments include brightness, contrast, saturation, grayscale and sepia.
These edits persist in local history/projects and participate in undo/redo. Select an
annotation to move, recolor or delete it. Undo/redo works from the toolbar or
Ctrl+Z/Ctrl+Y (Cmd+Z on macOS). Choose a zoom level for detailed work.
Escape cancels an unfinished drawing or move.
PNG/JPEG/WebP/GIF/BMP/PDF, Copy, Print and Share all use the current edits. Very long images
remain tiled; they do not require a page-sized canvas. Some large formats retain
their existing browser allocation limits.

**Save project** creates a `.winshot.json` file containing the original pixels and
editable annotations. Open it from **Open file** to continue editing in a new
document. Imported projects start a fresh undo history. Project/image imports
are limited to 100 MB; oversized or damaged files are rejected before History
is updated. The separate **Editable PNG** option embeds the browser project in a regular
PNG preview. **Open file** also accepts a restricted subset of native Windows
`.winshot` projects, rejecting unsupported edits rather than silently dropping
them. See [formats and compatibility](image-formats.md).

Redact covers pixels with an opaque rectangle. Edited PDFs omit the original
searchable text and link layers to prevent exposing covered or cropped content.
The original image remains in the local capture and editable project. Share a
flattened image/PDF rather than an editable project or Editable PNG when sharing
a redaction. Blur is a visual effect, not permanent redaction.

**Share** supports an email draft with an edited-image attachment, OneNote
clipboard paste, offline HTML notes, and system file sharing/drag where available.
See [sharing](sharing.md) for the exact workflows and client limits.

## Video studio

Open **Record & edit video** from the popup, or **Video studio** from history.
Record using Chrome's screen,
window or tab chooser, or import a video file supported by Chrome. Review the
video, step through frames to set the trim, crop, rotate/flip, change playback
speed and audio settings, then export WebM/MP4 or download the original. Sharing audio depends on the chosen source and Chrome/OS support.
The user makes the sharing choice; recording stops when they stop sharing.

Fast export decodes and encodes frames in an offline worker using the bundled
Mediabunny library; it does not need playback or a visible tab. Available MP4
codecs depend on Chrome and the machine. Compatibility WebM plays and re-records
the selected segment, so it takes about the clip length and needs a visible tab.
Speed changes also change audio pitch. This is not a multitrack timeline editor. Recording/import/export remain in the browser; no native app
handoff is involved. Saved originals remain available in local video history.
Recording continues when Chrome activates the shared source tab, even if the
studio's live preview cannot play. If Chrome cannot reopen a new recording,
**Save original** keeps its raw bytes available; save them before closing the tab.

## Optional desktop application

The desktop app is still useful for capturing other applications and desktop-only
editing features. **Capture browser window** retains its optional external
launch for browser chrome/window pixels. Page screenshots and browser editing
work independently of that launch.
