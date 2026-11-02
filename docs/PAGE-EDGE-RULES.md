# Page-edge rules

Isaiah, 2026-11-02: "Go see how Drawboard handles when things go over a page versus
how we handle when things try to go over a page, especially the rotation handle...
us having a rotation handle get cut off is definitely a mistake."

These rules say what a selection, a drag, a resize and a turn do where a mark meets
the edge of the picture. In WinShot the "page" is the captured image. The reference
app is Drawboard PDF, and the same rules apply to Survey's PDF pages.

## What we saw

- **WinShot before this change:** the selection frame and grabbers sat on
  `InteractionCanvas`, which is cut off at the image edge. On a mark near the top
  of the image the side grabbers and the rotate button were cut in half or missing
  (Isaiah's screenshot, 2026-11-02). A moved mark followed the pointer off the
  image and snapped back on release.
- **Survey:** the same cut-off rotate button at the top of a PDF page (Isaiah's
  screenshot, 2026-11-02).
- **Drawboard PDF:** the frame, grabbers and rotate button draw over everything,
  outside the page, in the grey margin (Isaiah's screenshot, 2026-11-02). In the
  Drawboard web app a moved mark stops at the page edge (measured for Survey,
  2026-10-06).
- **Elsewhere:** Okular once let a mark be dragged off its page, where nobody
  could see or grab it again (KDE bug 397222). That is why a move stops at the
  edge. Acrobat, PowerPoint and Figma all draw selection chrome outside the
  canvas.

## The rules

1. **The selection chrome is never cut off by the image edge.** The dashed frame,
   the eight grabbers, the rotate button, the bend grabber, the lock badge and the
   magnifier's focus ring draw over the desk around the image. They can be grabbed
   there too.
   - **1b. The rotate button can always be reached.** It floats above the frame.
     It moves below the frame only when the view can never show it above. That
     happens on a mark at the top of the image, where the clamped view leaves only
     a little desk, or where the settings row covers the top of the canvas.
     Grabbing it below turns the mark from where it is. The mark never flips over.
     Panning never moves the button. Only the image's own edge does.
2. **A mark is cut off at the image edge.** The part outside the image is not drawn
   and is not in the saved picture.
3. **A move stops at the image edge.** This holds on every frame of the drag, for
   one mark, for a group moved as one piece, and for the arrow keys. Nothing sails
   off the image and snaps back on release. A mark that already hangs over an edge
   is never pulled back in.
4. **A resize or a turn may take part of a mark past the edge.** That part is cut
   off (rule 2). The grabbers stay visible over the edge (rule 1), so the user can
   pull it straight back. Nothing fights the pointer in the middle of a gesture.

## Where it lives

- `src/WinShot/Editor/EditorWindow.xaml`: `ChromeCanvas`, an unclipped layer
  above `InteractionCanvas` that holds the selection chrome. Grabbers are
  hit-tested by geometry (`HitTestHandle`), so presses on the desk already reach
  them.
- `src/WinShot/Editor/RotateGripPlacement.cs` and `ResolveRotateGrip` in
  `EditorWindow.Handles.cs`: rule 1b. `BeginResize` and `DragRotate` keep the grab
  offset.
- `src/WinShot/Editor/PageEdgeMove.cs` and `ClampMoveToSurface`: rule 3.
- `tests/WinShot.Tests/PageEdgeRulesTests.cs`.

## Open

- Rule 4 is our own call. Drawboard's resize at the edge was not measured, because
  its app needs a sign-in. If Isaiah wants a resize to stop at the edge the way a
  move does, the resize maths in `EditorWindow.Handles.cs` needs to change.
- A callout's text box dragged on its own keeps its existing rules.
