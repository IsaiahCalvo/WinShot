# Scrolling capture: fixed regions and edge-case audit

## What changed

The old footer check walked upward while every row hash was byte-identical. One blinking
caret or small repaint stopped that walk, so the rest of the fixed prompt/footer was treated
as new document content and repeated after every scroll.

WinShot now builds a small change mask for every row (or column for horizontal capture):

- `same = matching sampled pixels at the same viewport position / samples`
- `shifted = matching sampled pixels after applying the measured scroll offset / samples`
- A line is fixed when `same >= 0.88` and, where both comparisons exist,
  `same - shifted >= 0.08`.
- Two to eight short changing lines are tolerated inside a band, so carets and spinners do
  not split it. At least two visually structured lines are required, and a band is capped at
  one third of the viewport to avoid consuming ordinary blank content.

In plain English: document pixels match after moving them by the scroll distance; fixed UI
matches without moving. That difference identifies the boundary. Fixed leading chrome is
kept from the first frame, fixed trailing chrome is removed from intermediate slices and
copied once from the final frame. The preview retracts the same pixels as the final stitch.

The overlap matcher remains deliberately lightweight:

1. Find the longest distinctive exact row/column run.
2. If browser text was re-rasterized, compare row-gradient profiles with normalized
   correlation (subtract each profile's average, multiply matching deviations, divide by
   their energy). Accept only a strong, unique peak confirmed by mean brightness and multiple
   column strips.
3. Place a straight seam at the first newly revealed row/column, outside the fixed band.
   A graph-cut seam would add cost and can blur or bend text; aligned UI capture needs a hard,
   verified boundary instead.

No dependency, network path, upload, OCR, or persistent background work was added.

## Research used

- [W3C CSS Positioned Layout](https://www.w3.org/TR/css-position-3/) defines fixed content as
  viewport-relative and sticky content as constrained to the nearest scrollport. This is why
  the same-position versus shifted-position test is meaningful.
- [Android ScrollCaptureCallback](https://developer.android.com/reference/android/view/ScrollCaptureCallback.html)
  treats the scroll bounds and scroll delta as separate facts, and says bounds should contain
  only content that tracks with scrolling. WinShot has no app-provided bounds, so the change
  mask estimates the equivalent inset from pixels.
- [OpenCV template matching](https://docs.opencv.org/master/de/da9/tutorial_template_matching.html)
  documents normalized cross-correlation. WinShot uses the same idea on compact 1-D gradient
  profiles instead of adding OpenCV or scanning full 2-D images.
- [OpenCV phase correlation](https://docs.opencv.org/3.4.20/d7/df3/group__imgproc__motion.html)
  is a translation estimator based on the Fourier shift theorem. It is robust for photographs
  but unnecessary for axis-aligned scrolling and would require larger buffers and a dependency.
- [OpenCV seam estimation](https://docs.opencv.org/3.4/d7/d09/classcv_1_1detail_1_1SeamFinder.html)
  lists dynamic-programming and graph-cut seam families. Those are useful for parallax and
  exposure differences, not normally for pixel-aligned UI rows.
- [ShareX scrolling screenshot notes](https://getsharex.com/docs/scrolling-screenshot) confirm
  the standard crop-top/append-bottom method and call out static and animated elements as
  known failure sources.

## Edge-case matrix

| Case | Status | Evidence / boundary |
|---|---|---|
| Fixed footer (ChatGPT-style prompt) | Handled | `FixedFooter_WithBlinkingCaret_IsAttachedExactlyOnce` asserts continuous body rows and one final footer. |
| Fixed header | Handled | Leading band is excluded from matching and retained only from frame one. |
| Fixed header + footer | Handled | `FixedHeaderAndTranslucentFooter_AppearOnceAroundContinuousBody`. |
| Blinking caret / small spinner in fixed UI | Handled | Per-line pixel ratio tolerates small local changes. |
| Partially transparent sticky UI | Partially handled | High-opacity/local transparency is covered. If most pixels reveal moving content, the bitmap alone may not expose a reliable fixed boundary. |
| Animated ad/video inside scrolling content | Handled when enough stable overlap remains | `AnimatedVideoInsideDocument_DoesNotMoveTheChosenSeam`; large full-viewport animation can still make alignment impossible. |
| Animated video filling a fixed band | Partially handled | Small changing areas are tolerated; a mostly changing band is intentionally not classified as fixed. |
| Repeated text, grids, or patterns | Safe refusal | Existing distinctive-run/unique-peak tests reject ambiguous offsets instead of guessing. |
| Variable scroll distance | Handled | Scripted service tests use uneven 19-52 px steps and existing velocity tests cover 1,200-5,000 px/s. |
| Overscroll, bounce, reverse, then resume | Handled | Existing reverse/review and fast-flick recovery tests assert no duplicated rows or columns. |
| Moving scrollbars | Handled | Side-trimmed matching plus `Detector_FindsAnimatedHeaderAndFooter_WithoutTreatingScrollbarAsChrome`. |
| Fractional-DPI / browser re-rasterization | Handled within tolerance | `FractionalDpiStyleRerasterization_StillFindsTheOffset`; a frame-size change itself is unsupported. |
| Horizontal scrolling | Handled | Fixed left/right bands are removed and the final right band is attached once. |
| Nested scroll container with fixed side panels | Partially handled | The moving center controls alignment. Arbitrary interior/side overlays are not removed from each appended slice. |
| Lazy-loaded content only in newly revealed rows | Handled | `LazyLoadedNewRows_DoNotDisturbTheExistingOverlap`. |
| Reflow/change across the overlap | Safe refusal / partial | `ReflowAcrossTheOverlap_IsRejectedInsteadOfInventingASeam`; user must slow down or scroll back to re-lock. |
| End-of-page no motion | Handled | Identical frames add nothing; auto mode keeps its existing multi-method end check. |
| Blank/whitespace stretch | Handled in calibrated auto mode; partial manual | Auto uses measured pixels-per-notch. Manual mode cannot infer motion from identical blank pixels. |
| Arbitrary floating widget in the middle of the viewport | Unsupported removal | It can be ignored for alignment when other strips agree, but correct one-time 2-D compositing needs region segmentation or app/DOM metadata. |
| Capture region changes size or DPI mid-run | Unsupported | Frames with different dimensions are rejected; restarting capture is safest. |

## Recommended future tests

- Real app-provided scroll bounds when Windows exposes a cross-application equivalent to
  Android's API; this could remove interior fixed overlays without image inference.
- Multi-frame voting for a mostly transparent footer over high-contrast moving content.
- A synthetic two-dimensional mask/compositor experiment for floating chat buttons and fixed
  sidebars, guarded by a strict confidence gate and memory benchmark.
- Mixed-monitor capture where the selected region physically crosses a DPI boundary during a
  session (off-screen reproduction needs a deterministic scale-transition fixture).
