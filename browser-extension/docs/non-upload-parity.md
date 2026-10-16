# Non-upload completion scope

The requested target is the non-upload functionality in FireShot Pro's official
[feature comparison](https://getfireshot.com/features.php). Twitter, Flickr,
EasyCaptures, ImageShack, FTP, and custom HTTP upload integrations are excluded.
Previous capture-only scope decisions do not close the remaining items below.

| Area | Existing capability | Remaining completion work |
| --- | --- | --- |
| Capture | Visible/full-page/region/element/panels/frames, batches, PDF | Repeat the public failures on each candidate; GSAP stabilization, zero-size floating containers, transient layout recovery, canvas snapshots, and loading-only frame selection now have regression tests |
| Desktop connection | Convenience external URL | Acknowledged native messaging, actual window pixels, editor handoff and Windows integration test |
| Editing | Native Windows shapes/styles/counters/undo/resize/projects; browser preview only | Accessible browser annotation workflow with styles, undo/redo, crop/resize, multiple documents and persistent editable annotations |
| Saving | PNG/JPEG/WebP, searchable linked PDF, clipboard, print | GIF/BMP, editable PNG metadata, external editor/native handoff, email drafts and drag-file export |
| Loading | Native image/clipboard/project import | Browser image/clipboard/project import, reopening editable PNGs |
| Options | Popup settings | Dedicated options/batch setup, render wait/speed, remembered per-mode output action |
| Integration | Browser commands, history | Opt-in page API and OneNote handoff where the client is installed |
| Installed-machine validation | Isolated Chrome on Linux/Windows CI | Actual installed Chrome profile, native app and third-party client acceptance require a connection to that Windows machine |

Each completed claim requires an interaction/output check. External client
availability and the installed user profile must remain explicitly unverified
until the corresponding machine/client can be exercised. A passing API stub is
not a substitute for that verification.
