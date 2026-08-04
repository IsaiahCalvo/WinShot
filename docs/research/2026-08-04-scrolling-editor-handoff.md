# Scrolling editor handoff revision

## Cause and ownership

Direct Edit previously started History and auto-copy readers against the same bitmap that the
editor owned. Those readers could clone or encode while the editor was converting or disposing
the bitmap, so the editor could open without a usable preview even though History saved correctly.

Direct Edit now has one authoritative GDI bitmap. The editor first creates frozen WPF preview
tiles on a worker thread. History and auto-copy are then scheduled together but borrow the bitmap
one at a time. Editing remains disabled until both readers finish, and closing early defers bitmap
disposal until pending work releases it. A centralized source-operation lifetime now covers initial
load plus every later effect, transform, crop/undo source replacement, and borrowed refresh. Close
waits for all root and nested leases before disposing the editor's owned bitmaps.

Every preview tile is normalized to 96 DPI and explicitly laid out at its pixel width and height.
This preserves the editor's one-WPF-unit-per-source-pixel coordinate model even when source bitmap
metadata reports 120, 144, or 192 DPI.

The 32-bit scrolling path creates no full-resolution GDI clones. Its pixel-buffer peak is:

- Without auto-copy: source + frozen preview tiles + one tile scratch buffer.
- With auto-copy: source + frozen preview tiles + one source-sized DIB. The scratch buffer has
  already been returned before auto-copy starts.

At the 512 MiB capture limit with 4096px width and 2048px tiles, those pixel buffers are 1056 MiB
without auto-copy and 1536 MiB with auto-copy. PNG encoder, WPF object, and process baseline
overhead are additional. Non-32-bit History inputs use one bounded conversion tile, not a full-size
fallback clone.

## Off-screen evidence

The opt-in regression harness calls the real private `App.HandleCapture` through a thin test seam,
with direct Edit, scrolling History, and auto-copy enabled. App startup side effects are suppressed,
and the auto-copy callback builds a source-sized synthetic DIB instead of changing the user's
clipboard. Both editor windows are non-activating and positioned off-screen.

For a 1024x16384 BGRA32 fixture (64 MiB):

- `HandleCapture` returned in 974 ms.
- Initial preview was ready in 1271 ms.
- Preview, History, and synthetic auto-copy finished in 1600 ms.
- The UI dispatcher responded during post-capture work.
- Peak private memory was 328.7 MiB, a 291.4 MiB increase from the pre-source baseline.
- Managed allocations through the handoff were 77.2 MiB.
- Direct Edit rendered the tall image, remained editable, and History Edit rendered the saved copy.
- 120/144/192-DPI sources rendered at exact pixel dimensions with intact tile boundaries, full
  top/bottom coverage, and pixel-aligned annotation coordinates.
- A deliberately paused post-initial blur refresh survived window close, completed without a
  disposed-bitmap error, and released the source after its final lease.

The full Release suite passed 539/539 on a separate hidden Windows desktop.
