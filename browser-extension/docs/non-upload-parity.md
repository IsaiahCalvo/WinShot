# Non-upload completion scope

The requested target is the non-upload functionality in FireShot Pro's official
[feature comparison](https://getfireshot.com/features.php). Twitter, Flickr,
EasyCaptures, ImageShack, FTP, and custom HTTP upload integrations are excluded.
Previous capture-only scope decisions do not close the remaining items below.

## Browser-first editing decision

Screenshots and recordings are edited in extension-owned Chrome tabs. The Windows
application is optional; browser editing, local projects, recording and video
trimming must work without a protocol launch, native messaging, or an installed
desktop application. The native application remains useful for optional desktop
captures and its additional editing tools. Its bridge is not a prerequisite for
the browser editor and is not counted as a missing browser editing step.

| Area | Existing capability | Remaining completion work |
| --- | --- | --- |
| Capture | Visible/full-page/region/element/panels/frames, batches, PDF | Repeat the public failures on each candidate; GSAP stabilization, zero-size floating containers, transient layout recovery, canvas snapshots, and loading-only frame selection now have regression tests |
| Desktop connection | Optional convenience external URL for browser-window capture | Optional acknowledged desktop integration and validation of actual window pixels; not required for browser editing |
| Editing | Browser annotations, drawing, text, opaque redaction, crop, resize, 90° rotation, flips, local blur, brightness/contrast/saturation/grayscale/sepia, undo/redo and locally saved editable projects | Full Windows-editor tool matching and additional specialized effects |
| Video | Browser recording/import, frame-step trim, crop/rotate/flip/speed/audio, offline WebCodecs WebM/MP4 export and compatibility WebM | Multitrack editing and additional specialized effects/codecs; available MP4 codecs vary by machine |
| Saving | PNG/JPEG/WebP/GIF/BMP, searchable linked unedited PDF, flattened edited PDF, clipboard, print, editable .winshot.json and PNG projects, attached-image email drafts and prepared file drag/share | Installed mail/share-sheet behavior varies by OS and application |
| Loading | Browser image/clipboard/project import, reopening WinShot editable PNGs, restricted native Windows project subset and video files | Unsupported native annotation styles and other applications’ proprietary project metadata |
| Options | Per-mode remembered editor/save action; selected/current/all-window batch scope; individual output ZIP or combined PDF | Dedicated options page and adjustable scrolling/render timing |
| Integration | Browser commands, history, explicit-origin opt-in page API, OneNote image/HTML/text clipboard flow, offline image notes and system file sharing when supported | Direct cloud OneNote upload is not included; paste in OneNote after copying |
| Installed-machine validation | Isolated Chrome on Linux/Windows CI | Actual installed Chrome profile and optional desktop/client integrations require a connection to that Windows machine; browser editing does not depend on them |

Each completed claim requires an interaction/output check. External client
availability and the installed user profile must remain explicitly unverified
until the corresponding machine/client can be exercised. A passing API stub is
not a substitute for that verification.

Edited exports contain the flattened edited image. Their source PDF text/link
layers are omitted so cropped or redacted source text cannot remain selectable.
Original pixels and editable objects remain in the local project; sharing an
editable project also shares its original image. Share an exported image/PDF when
the original must not be included. Project imports reset session undo history.
