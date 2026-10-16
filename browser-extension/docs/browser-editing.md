# Edit locally in a Chrome tab

WinShot Capture does not need the Windows application to edit a screenshot or
recording. Page captures open the image editor automatically. Use **Open image
or project** in the popup, or **Open file** in the editor, to bring in an image,
paste an image, or reopen an editable browser project.
Each document has its own tab, and edits are saved in the extension's local
history. No file is uploaded.

## Image editor

Use Select, Arrow, Rectangle, Ellipse, Pen, Text, Redact, and Crop. Select an
annotation to move, recolor or delete it. Undo/redo works from the toolbar or
Ctrl+Z/Ctrl+Y (Cmd+Z on macOS). Choose a zoom level for detailed work.
Escape cancels an unfinished drawing or move.
PNG/JPEG/WebP/PDF, Copy and Print all use the current edits. Very long images
remain tiled; they do not require a page-sized canvas. Some large formats retain
their existing browser allocation limits.

**Save project** creates a `.winshot.json` file containing the original pixels and
editable annotations. Open it from **Open file** to continue editing in a new
document. Imported projects start a fresh undo history. Project/image imports
are limited to 100 MB; oversized or damaged files are rejected before History
is updated. This is a browser project format, not the native Windows project
format or editable metadata embedded in PNG files.

Redact covers pixels with an opaque rectangle. Edited PDFs omit the original
searchable text and link layers to prevent exposing covered or cropped content.
The original image remains in the local capture and editable project. Share a
flattened image/PDF rather than an editable project when sharing a redaction.

## Video studio

Open **Record & edit video** from the popup, or **Video studio** from history.
Record using Chrome's screen,
window or tab chooser, or import a video file supported by Chrome. Review the
video, select the start and end of the clip, and export WebM, or download the
original. Sharing audio depends on the chosen source and Chrome/OS support.
The user makes the sharing choice; recording stops when they stop sharing.

Clipped export plays and re-records the selected segment, so export takes about
the length of the clip. It is not a lossless or frame-accurate professional
timeline editor. Recording/import/export remain in the browser; no native app
handoff is involved. Saved originals remain available in local video history.
Recording continues when Chrome activates the shared source tab, even if the
studio's live preview cannot play. If Chrome cannot reopen a new recording,
**Save original** keeps its raw bytes available; save them before closing the tab.

## Optional desktop application

The desktop app is still useful for capturing other applications and desktop-only
editing features. **Capture browser window** retains its optional external
launch for browser chrome/window pixels. Page screenshots and browser editing
work independently of that launch.
