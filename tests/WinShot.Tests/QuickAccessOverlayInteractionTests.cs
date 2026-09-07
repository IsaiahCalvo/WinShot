using System.Globalization;
using System.Reflection;
using System.IO;
using System.Xml.Linq;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using W = System.Windows;
using WF = System.Windows.Forms;
using WM = System.Windows.Media;

namespace WinShot.Tests;

public class QuickAccessOverlayInteractionTests
{
    [Fact]
    public void ActionsExposeNamesAndKeyboardPaths()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            bool pin = false;
            bool annotate = false;
            overlay.PinRequested += _ => pin = true;
            overlay.EditRequested += _ => annotate = true;

            Assert.Equal("Quick Access capture preview", overlay.AccessibleName);
            Assert.Equal(5, overlay.AccessibilityObject.GetChildCount());
            Assert.Equal(
                new[] { "Pin", "Close", "Annotate", "Save", "Copy" },
                Enumerable.Range(0, 5).Select(i => overlay.AccessibilityObject.GetChild(i)!.Name).ToArray());
            Assert.Equal("Close (Ctrl+W)", overlay.AccessibilityObject.GetChild(1)!.Help);
            Assert.Equal("Open Annotate tool (Ctrl+E)", overlay.AccessibilityObject.GetChild(2)!.Help);
            Assert.Equal("Save (Ctrl+S)", overlay.AccessibilityObject.GetChild(3)!.Help);

            InvokeKey(overlay, WF.Keys.Control | WF.Keys.E);
            Assert.True(annotate);

            InvokeKey(overlay, WF.Keys.Tab);
            InvokeKey(overlay, WF.Keys.Enter);
            Assert.True(pin);
        });
    }

    [Fact]
    public void RequestedCornerLayoutUsesSuppliedSaveSvgAndCentersCopy()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var buttons = GetButtons(overlay);

            var pin = Button(buttons, "Pin");
            var close = Button(buttons, "Close");
            var annotate = Button(buttons, "Annotate");
            var save = Button(buttons, "Save");
            var copy = Button(buttons, "Copy");

            Assert.Equal(pin.Bounds.Left, annotate.Bounds.Left);
            Assert.Equal(close.Bounds.Left, save.Bounds.Left);
            Assert.Equal(pin.Bounds.Top, close.Bounds.Top);
            Assert.Equal(annotate.Bounds.Top, save.Bounds.Top);
            Assert.True(save.IconLoaded);
            Assert.InRange(copy.Bounds.Left + copy.Bounds.Width / 2, overlay.ClientSize.Width / 2 - 1, overlay.ClientSize.Width / 2 + 1);
            Assert.InRange(copy.Bounds.Top + copy.Bounds.Height / 2, overlay.ClientSize.Height / 2 - 1, overlay.ClientSize.Height / 2 + 1);
            Assert.Equal(new SD.Size(78, 27), copy.Bounds.Size);
            Assert.Equal(new SD.Size(14, 14), copy.IconSize);
            Assert.Equal(7f, copy.LabelFontSize);
        });
    }

    [Fact]
    public void CustomTooltipIsRoundedAndCannotActivate()
    {
        RunSta(() =>
        {
            var visuals = QuickAccessOverlayThemePalette.For(QuickAccessOverlayTheme.Dark);
            using var tooltip = new QuickAccessTooltipWindow("Save", visuals, 96);
            var createParams = (WF.CreateParams)typeof(QuickAccessTooltipWindow)
                .GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(tooltip)!;
            var showWithoutActivation = (bool)typeof(QuickAccessTooltipWindow)
                .GetProperty("ShowWithoutActivation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(tooltip)!;

            Assert.True(showWithoutActivation);
            Assert.NotEqual(0, createParams.ExStyle & 0x08000000);
            Assert.NotEqual(0, createParams.ExStyle & 0x00000080);
            Assert.InRange(tooltip.ClientSize.Width, 25, 55);
            Assert.InRange(tooltip.ClientSize.Height, 16, 26);
        });
    }

    [Fact]
    public void FixedCardCenterCropsWideCapturesInsteadOfStretchingThem()
    {
        using var source = new SD.Bitmap(400, 100);
        using (var graphics = SD.Graphics.FromImage(source))
        {
            graphics.Clear(SD.Color.Red);
            graphics.FillRectangle(SD.Brushes.Lime, 120, 0, 160, 100);
        }

        var method = typeof(FastQuickActionsWindow)
            .GetMethod("CreatePreviewBitmap", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var preview = (SD.Bitmap)method.Invoke(null, new object[] { source, 192, 120, false })!;

        SD.Color left = preview.GetPixel(4, preview.Height / 2);
        SD.Color center = preview.GetPixel(preview.Width / 2, preview.Height / 2);
        Assert.True(left.G > left.R);
        Assert.True(center.G > center.R);
    }

    [Fact]
    public void DisposedOverlayClearsPreviewReferencesBeforeLateOffscreenPaint()
    {
        RunSta(() =>
        {
            using var source = new SD.Bitmap(400, 240);
            var overlay = FastQuickActionsWindow.CreateForTheme(
                source,
                new SettingsService(),
                QuickAccessOverlayTheme.Dark);
            var type = typeof(FastQuickActionsWindow);
            var previewField = type.GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var blurredField = type.GetField("_blurredPreview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            previewField.SetValue(overlay, new SD.Bitmap(192, 120));
            blurredField.SetValue(overlay, new SD.Bitmap(192, 120));

            overlay.Dispose();

            using var surface = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
            using var graphics = SD.Graphics.FromImage(surface);
            using var args = new WF.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, surface.Size));
            type.GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, new object[] { args });
            Assert.Null(previewField.GetValue(overlay));
            Assert.Null(blurredField.GetValue(overlay));
        });
    }

    [Fact]
    public void LatePreviewLoadAfterDisposeCannotReattachDisposedWindowImages()
    {
        RunSta(() =>
        {
            using var source = new SD.Bitmap(400, 240);
            var overlay = FastQuickActionsWindow.CreateForTheme(
                source,
                new SettingsService(),
                QuickAccessOverlayTheme.Dark);
            var type = typeof(FastQuickActionsWindow);
            var previewField = type.GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var blurredField = type.GetField("_blurredPreview", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var latePreview = new SD.Bitmap(192, 120);
            var lateBlurred = new SD.Bitmap(192, 120);

            overlay.Dispose();
            type.GetMethod("InstallPreviewBitmaps", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, new object[] { latePreview, lateBlurred });

            Assert.Null(previewField.GetValue(overlay));
            Assert.Null(blurredField.GetValue(overlay));
            Assert.Throws<ArgumentException>(() => _ = latePreview.RawFormat);
            Assert.Throws<ArgumentException>(() => _ = lateBlurred.RawFormat);
        });
    }

    [Fact]
    public void OverflowKeepsOcrAndBackgroundWithoutCloud()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var field = typeof(FastQuickActionsWindow).GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var menu = (WF.ContextMenuStrip)field.GetValue(overlay)!;
            string[] labels = menu.Items.Cast<WF.ToolStripItem>()
                .Where(i => i is not WF.ToolStripSeparator)
                .Select(i => i.Text ?? string.Empty)
                .ToArray();

            Assert.Contains("Extract text", labels);
            Assert.Contains("Add background", labels);
            Assert.Contains("Rotate left", labels);
            Assert.Contains("Flip horizontal", labels);
            Assert.Contains("Resize…", labels);
            Assert.Contains("Print…", labels);
            Assert.Contains("Open with…", labels);
            Assert.Contains("Move to Recycle Bin", labels);
            Assert.Contains("Save all…", labels);
            Assert.Contains("Close all", labels);
            Assert.Contains("Temporarily hide", labels);
            // Cloud upload and Quick Look are the CleanShot rows we skipped — Quick Look
            // because Pin already is one.
            Assert.DoesNotContain(labels, l => l.Contains("Cloud", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(labels, l => l.Contains("Quick Look", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void RotateSwapsTheCardImageAndBumpsTheRenderVersion()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var type = typeof(FastQuickActionsWindow);
            var run = type.GetMethod("RunMenuAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var versionField = type.GetField("_imageVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;

            run.Invoke(overlay, new object[] { "rotate-right", "Rotate right" });
            PumpUntilRenderIdle(overlay);

            using var rotated = overlay.CloneImage();
            Assert.Equal(760, rotated.Width);
            Assert.Equal(1200, rotated.Height);
            Assert.Equal(1, (int)versionField.GetValue(overlay)!);
            Assert.False(overlay.IsDisposed); // the card stays up, showing the edited capture
        });
    }

    [Fact]
    public void MenuRowsShareOneWidthAndGutterEvenly()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var menu = (WF.ContextMenuStrip)typeof(FastQuickActionsWindow)
                .GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!;
            menu.PerformLayout();

            // Even gutters: the highlight spans a row, so an uneven pad shows as a
            // highlight that touches one border and stops short of the other.
            Assert.Equal(menu.Padding.Left, menu.Padding.Right);

            // One width for every row. The width is cached, and it was once cached
            // mid-build, leaving early rows wide and later ones short.
            int[] widths = menu.Items.Cast<WF.ToolStripItem>()
                .Where(i => i is not WF.ToolStripSeparator)
                .Select(i => i.GetPreferredSize(SD.Size.Empty).Width)
                .Distinct()
                .ToArray();
            Assert.Single(widths);
            Assert.True(
                widths[0] + menu.Padding.Horizontal <= menu.GetPreferredSize(SD.Size.Empty).Width,
                "the drop-down must be wide enough for its rows");
        });
    }

    [Fact]
    public void HoverHighlightInsetsEquallyFromBothMenuEdges()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var menu = (WF.ContextMenuStrip)typeof(FastQuickActionsWindow)
                .GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!;
            menu.PerformLayout();

            var bounds = menu.Items[0].GetType()
                .GetMethod("HighlightBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var row in menu.Items.Cast<WF.ToolStripItem>().Where(i => i is not WF.ToolStripSeparator))
            {
                var highlight = (SD.Rectangle)bounds.Invoke(row, null)!;
                // Converted out of the row's coordinates and into the menu's, where the
                // gaps have to match: rows are not placed evenly inside the drop-down, so
                // measuring the highlight against the row itself hides the asymmetry.
                int left = row.Bounds.X + highlight.Left;
                int right = menu.ClientSize.Width - (row.Bounds.X + highlight.Right);
                Assert.Equal(left, right);
                Assert.True(left > 0, $"'{row.Text}' highlight runs into the menu border");
            }
        });
    }

    [Fact]
    public void CardKeepsAHairlineEdgeOverANearBlackCapture()
    {
        RunSta(() =>
        {
            using var black = new SD.Bitmap(1200, 760);
            using (var g = SD.Graphics.FromImage(black))
                g.Clear(SD.Color.FromArgb(6, 6, 8));

            using var overlay = FastQuickActionsWindow.CreateForTheme(
                black, new SettingsService(), QuickAccessOverlayTheme.Dark);
            overlay.CreateControl();
            using var surface = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
            using (var g = SD.Graphics.FromImage(surface))
            using (var args = new WF.PaintEventArgs(g, new SD.Rectangle(SD.Point.Empty, surface.Size)))
            {
                // An opaque ground first: the card paints onto the desktop, and measuring it
                // over a transparent bitmap reads back the compositing, not the pixels a
                // person sees — which made the two edges look different when they are not.
                g.Clear(SD.Color.Black);
                typeof(FastQuickActionsWindow).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(overlay, new object[] { args });
            }

            int middle = surface.Height / 2;
            int interior = surface.GetPixel(surface.Width / 2, middle).R;
            int left = Brightest(surface, 0, 3, middle);
            int right = Brightest(surface, surface.Width - 3, surface.Width, middle);

            // Faint, but a capture this dark has no edge of its own to read.
            Assert.True(left > interior + 20, $"left edge {left} vs interior {interior}");
            Assert.Equal(left, right);
            Assert.True(left < 90, $"the edge is meant to be a hairline, not a frame: {left}");
            Assert.Equal(interior, Brightest(surface, surface.Width / 2 - 2, surface.Width / 2 + 2, middle));
        });
    }

    /// <summary>
    /// Until the failure paths landed, the only string this surface ever drew was "Text
    /// copied" — two words, nowhere near an edge, which is why nobody noticed the layout rect
    /// was _cardRect with no inset at all while every other element on the card sits inside
    /// ControlPadding. Scanned rather than eyeballed, per HANDOFF: the ink is isolated by
    /// rendering the same flash with an empty message and diffing, so the card fill, the
    /// scrim and the edge hairline all cancel out.
    ///
    /// Every message FriendlyError can put here is checked, plus a run with no space in it —
    /// which is what makes the scan mean something. GDI+ breaks a word too long for the
    /// layout rect by character, filling the full width, so that case reaches the card's
    /// edge exactly when the rect is not inset and stays in the margin when it is.
    /// </summary>
    [Fact]
    public void CardStatusFlashStaysInsideTheCardPadding()
    {
        RunSta(() =>
        {
            using var capture = new SD.Bitmap(1200, 760);
            using var overlay = FastQuickActionsWindow.CreateForTheme(
                capture, new SettingsService(), QuickAccessOverlayTheme.Dark);
            overlay.CreateControl();

            var card = (SD.Rectangle)typeof(FastQuickActionsWindow)
                .GetField("_cardRect", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
            var padding = (int)typeof(FastQuickActionsWindow)
                .GetField("_controlPadding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay)!;
            SD.Rectangle inside = SD.Rectangle.Inflate(card, -padding, -padding);

            using SD.Bitmap blank = PaintFlash(overlay, string.Empty);
            foreach (string message in FlashMessages())
            {
                using SD.Bitmap painted = PaintFlash(overlay, message);
                SD.Rectangle ink = InkBounds(painted, blank);

                Assert.False(ink.IsEmpty, $"'{message}' drew nothing at all");
                Assert.True(ink.Left >= inside.Left, $"'{message}' starts {inside.Left - ink.Left}px into the left margin");
                Assert.True(ink.Right <= inside.Right, $"'{message}' runs {ink.Right - inside.Right}px past the right margin");
                Assert.True(ink.Top >= inside.Top, $"'{message}' starts {inside.Top - ink.Top}px above the top margin");
                Assert.True(ink.Bottom <= inside.Bottom, $"'{message}' runs {ink.Bottom - inside.Bottom}px past the bottom margin");
            }
        });
    }

    /// <summary>Everything the status flash can be asked to draw: the confirmation it was
    /// built for, every friendly failure line the card's actions route to it, and an
    /// unbreakable run to make the layout rect prove it is inset.</summary>
    private static IEnumerable<string> FlashMessages()
    {
        yield return "Text copied";
        foreach (Exception ex in new Exception[]
        {
            new UnauthorizedAccessException(),
            new DirectoryNotFoundException(),
            new PathTooLongException(),
            new FileNotFoundException(),
            new IOException(),
            new System.Net.Http.HttpRequestException(),
            new OutOfMemoryException(),
            new ClipboardBusyException(null),
            new Exception("something nobody mapped"),
        })
            yield return FriendlyError.For(ex);

        yield return new string('W', 60);
    }

    /// <summary>The bounding box of everything that differs between two renders of the same
    /// card, which is exactly the message's ink.</summary>
    private static SD.Rectangle InkBounds(SD.Bitmap painted, SD.Bitmap blank)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < painted.Height; y++)
        {
            for (int x = 0; x < painted.Width; x++)
            {
                if (painted.GetPixel(x, y) == blank.GetPixel(x, y))
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return minX > maxX
            ? SD.Rectangle.Empty
            : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// The share failures are on the toast precisely because a toast can outlive the card —
    /// the sheet's error callback lands whenever Windows gets round to it, by which time the
    /// auto-close timer, an Escape or the next capture can all have taken the window. Asking
    /// the dead card where to sit threw ObjectDisposedException out of the toast call's first
    /// line, and the catch around it swallowed the failure the toast was reporting.
    /// </summary>
    [Fact]
    public void ToastAboutAClosedCardFallsBackToTheCorner()
    {
        RunSta(() =>
        {
            using var capture = new SD.Bitmap(1200, 760);
            var card = new FastQuickActionsWindow(capture, new SettingsService());
            card.Show();

            SD.Point? live = card.ToastAnchor();
            Assert.NotNull(live);
            SD.Rectangle onScreen = card.RectangleToScreen(card.ClientRectangle);
            // Centred on the card, not hung off its left edge: ToastWindow.Place reads an
            // anchor as the point to centre above.
            Assert.Equal(onScreen.Left + (onScreen.Width / 2), live!.Value.X);

            card.Dispose();
            Assert.Null(card.ToastAnchor());

            var method = typeof(FastQuickActionsWindow)
                .GetMethod("ShowShareToast", BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(card, new object[] { "Share failed", "The drive is full.", ToastSeverity.Error });
        });
    }

    /// <summary>
    /// Save is the action on this card with the highest consequence and was the last one with
    /// no failure feedback at all: the Close() sits inside the try after the await, so a throw
    /// skipped it and the card stayed exactly as it was — a failure indistinguishable from a
    /// click that missed, with the user believing the capture is on disk. It is also the only
    /// path FriendlyError's save-specific lines are reachable from.
    /// </summary>
    [Fact]
    public void ASaveThatFailsSaysSoOnTheCard()
    {
        RunSta(() =>
        {
            // A folder that cannot be created because a FILE sits where a directory would go.
            // Deterministic on every machine, unlike betting on a missing drive letter.
            string blocker = Path.Combine(Path.GetTempPath(), $"winshot-save-block-{Guid.NewGuid():N}");
            File.WriteAllText(blocker, string.Empty);
            try
            {
                using var bitmap = new SD.Bitmap(1200, 760);
                var settings = new SettingsService();
                settings.Current.SaveFolder = Path.Combine(blocker, "captures");
                using var overlay = new FastQuickActionsWindow(bitmap, settings);
                var type = typeof(FastQuickActionsWindow);
                var message = type.GetField("_flashMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var isFailure = type.GetField("_flashIsFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;

                // Directory.CreateDirectory throws before the first await, so the catch has
                // already run by the time the call returns.
                type.GetMethod("SaveCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(overlay, new object?[] { false, true, null, true });

                Assert.Equal(FriendlyError.For(new IOException()), message.GetValue(overlay));
                Assert.True((bool)isFailure.GetValue(overlay)!, "a failure flash must not resume the auto-close timer");
                Assert.False(overlay.IsDisposed, "the card closed as if the save had worked");
            }
            finally
            {
                File.Delete(blocker);
            }
        });
    }

    /// <summary>The hold scales with the message so a sentence is readable, and "Text copied"
    /// keeps exactly the beat it has always had.</summary>
    [Fact]
    public void StatusFlashHoldsLongerForALongerMessage()
    {
        int copied = FastQuickActionsWindow.StatusHoldMs("Text copied");
        int failure = FastQuickActionsWindow.StatusHoldMs(
            FriendlyError.For(new UnauthorizedAccessException()));

        Assert.Equal(1300, copied);
        Assert.True(failure >= 3000, $"a thirteen-word failure line holds only {failure}ms");
        Assert.True(failure <= 5200, $"a failure line holds {failure}ms, which is in the way");
        Assert.Equal(1300, FastQuickActionsWindow.StatusHoldMs(null));
    }

    private static SD.Bitmap PaintFlash(FastQuickActionsWindow overlay, string message)
    {
        overlay.FlashStatus(message);
        var surface = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
        using (var g = SD.Graphics.FromImage(surface))
        using (var args = new WF.PaintEventArgs(g, new SD.Rectangle(SD.Point.Empty, surface.Size)))
        {
            // An opaque ground, for the same reason the hairline test uses one: the card
            // paints onto the desktop, and a transparent bitmap reads back the compositing.
            g.Clear(SD.Color.Black);
            typeof(FastQuickActionsWindow).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, new object[] { args });
        }
        return surface;
    }

    private static int Brightest(SD.Bitmap surface, int fromX, int toX, int y)
    {
        int best = 0;
        for (int x = Math.Max(0, fromX); x < Math.Min(surface.Width, toX); x++)
            best = Math.Max(best, surface.GetPixel(x, y).R);
        return best;
    }

    [Fact]
    public void ReachingForTheCardDismissesTheStatusFlash()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var type = typeof(FastQuickActionsWindow);
            var message = type.GetField("_flashMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var origin = type.GetField("_flashCursor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var dismiss = type.GetMethod("DismissStatusFlashOnPointerMove", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // A pointer that has not moved keeps the message up: closing the context menu
            // delivers a MouseMove over a pointer sitting exactly where it was clicked.
            overlay.FlashStatus("Text copied");
            dismiss.Invoke(overlay, null);
            Assert.Equal("Text copied", message.GetValue(overlay));

            // A pointer that has travelled clears it, so the hover chrome takes over.
            origin.SetValue(overlay, new SD.Point(-10_000, -10_000));
            dismiss.Invoke(overlay, null);
            Assert.Null(message.GetValue(overlay));

            // And with no message up it is a no-op, not a crash.
            dismiss.Invoke(overlay, null);
            Assert.Null(message.GetValue(overlay));
        });
    }

    [Fact]
    public void StatusFlashClearsItselfAndFreesItsTimer()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var type = typeof(FastQuickActionsWindow);
            var message = type.GetField("_flashMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = type.GetField("_flashTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;

            overlay.FlashStatus("Text copied");
            Assert.Equal("Text copied", message.GetValue(overlay));
            Assert.NotNull(timer.GetValue(overlay));

            type.GetMethod("EndStatusFlash", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(overlay, null);

            Assert.Null(message.GetValue(overlay));
            Assert.Null(timer.GetValue(overlay));
            Assert.Equal(0d, (double)type.GetField("_flashAmount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!);
        });
    }

    [Fact]
    public void EveryIconedRowResolvesItsGlyph()
    {
        RunSta(() =>
        {
            string[] ids =
            [
                "annotate", "pin", "ocr", "background",
                "rotate-left", "rotate-right", "flip-h", "flip-v", "resize",
            ];
            foreach (string id in ids)
            {
                string asset = InvokeIconFor(id) ?? throw new Xunit.Sdk.XunitException($"{id} has no icon");
                Assert.NotNull(SvgIcons.Get(asset, 16, SD.Color.White));
            }
            Assert.Null(InvokeIconFor("print"));
        });
    }

    /// <summary>
    /// The recording strip's camcorder is the smallest glyph on the card, so it is the one
    /// that shows a mismatched stroke weight first. <see cref="SvgIcons"/> fits the whole
    /// viewBox into the square render box, so a viewBox with empty margin renders the
    /// artwork small and its 1.85 stroke light: on the stock 24x24 box the camcorder
    /// occupied 14 of 24 rows, so it drew at 0.58x and its stroke landed at 0.69 device px
    /// beside 1.08 px everywhere else on the same card.
    ///
    /// Measured off the asset rather than off a render: RenderTargetBitmap draws nothing on
    /// a bare test thread (no dispatcher, so no media context) — every glyph comes back
    /// blank there, which would make a pixel scan assert something that is true of the test
    /// host and not of the app.
    /// </summary>
    [Fact]
    public void TheRecordingGlyphsViewBoxIsCroppedToWhatItActuallyDraws()
    {
        using Stream stream = typeof(FastQuickActionsWindow).Assembly
            .GetManifestResourceStream("WinShot.Assets.quick-access-camcorder.svg")
            ?? throw new Xunit.Sdk.XunitException("the camcorder asset is not embedded");
        XElement root = XDocument.Load(stream).Root!;

        string[] box = root.Attribute("viewBox")!.Value.Split(' ');
        var viewBox = new W.Rect(
            double.Parse(box[0], CultureInfo.InvariantCulture),
            double.Parse(box[1], CultureInfo.InvariantCulture),
            double.Parse(box[2], CultureInfo.InvariantCulture),
            double.Parse(box[3], CultureInfo.InvariantCulture));

        // Half the stroke sits outside the path, so the ink is wider than the geometry.
        double half = double.Parse(root.Attribute("stroke-width")!.Value, CultureInfo.InvariantCulture) / 2d;
        W.Rect ink = W.Rect.Empty;
        foreach (XElement element in root.Descendants())
        {
            WM.Geometry? geometry = element.Name.LocalName switch
            {
                "path" => WM.Geometry.Parse(element.Attribute("d")!.Value),
                "rect" => new WM.RectangleGeometry(new W.Rect(
                    double.Parse(element.Attribute("x")!.Value, CultureInfo.InvariantCulture),
                    double.Parse(element.Attribute("y")!.Value, CultureInfo.InvariantCulture),
                    double.Parse(element.Attribute("width")!.Value, CultureInfo.InvariantCulture),
                    double.Parse(element.Attribute("height")!.Value, CultureInfo.InvariantCulture))),
                _ => null,
            };
            if (geometry is null)
                continue;
            W.Rect bounds = geometry.Bounds;
            bounds.Inflate(half, half);
            ink.Union(bounds);
        }

        Assert.True(viewBox.Contains(ink), $"the viewBox {viewBox} clips the ink {ink}");
        Assert.True(ink.Width / viewBox.Width > 0.95, $"the viewBox is {viewBox.Width} wide for {ink.Width} of ink");
        Assert.True(ink.Height / viewBox.Height > 0.95, $"the viewBox is {viewBox.Height} tall for {ink.Height} of ink");
    }

    private static string? InvokeIconFor(string id) =>
        (string?)typeof(FastQuickActionsWindow).Assembly
            .GetType("WinShot.Overlay.QuickActionsMenu")!
            .GetMethod("IconFor", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { id });

    /// <summary>
    /// A pixel edit awaits the in-flight thumbnail render before touching the bitmap, and
    /// that continuation comes back through the WinForms sync context — which needs a
    /// message pump the unshown test window does not otherwise get.
    /// </summary>
    private static void PumpUntilRenderIdle(FastQuickActionsWindow overlay)
    {
        var field = typeof(FastQuickActionsWindow)
            .GetField("_previewTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 600; i++)
        {
            WF.Application.DoEvents();
            if ((Task?)field.GetValue(overlay) is not { IsCompleted: false })
                return;
            Thread.Sleep(5);
        }
        throw new Xunit.Sdk.XunitException("The overlay thumbnail render never finished.");
    }


    [Fact]
    public void HoverPausesAndResumesAutoCloseTimer()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayAutoClose = true;
            settings.Current.OverlayAutoCloseSeconds = 20;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            var timerField = typeof(FastQuickActionsWindow).GetField("_autoCloseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timer = (WF.Timer)timerField.GetValue(overlay)!;

            Assert.True(timer.Enabled);
            Invoke(overlay, "SetHovering", true);
            Assert.False(timer.Enabled);
            Invoke(overlay, "SetHovering", false);
            Assert.True(timer.Enabled);
        });
    }

    [Fact]
    public void OnlyOneStackedCardKeepsItsHoverActionsExpanded()
    {
        RunSta(() =>
        {
            using var firstBitmap = new SD.Bitmap(1200, 760);
            using var secondBitmap = new SD.Bitmap(1200, 760);
            using var first = new FastQuickActionsWindow(firstBitmap, new SettingsService());
            using var second = new FastQuickActionsWindow(secondBitmap, new SettingsService());
            var hovering = typeof(FastQuickActionsWindow).GetField("_hovering", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Invoke(first, "SetHovering", true);
            Invoke(second, "SetHovering", true);

            Assert.False((bool)hovering.GetValue(first)!);
            Assert.True((bool)hovering.GetValue(second)!);
        });
    }

    [Fact]
    public void NativeWindowClassRequestsDropShadow()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var property = typeof(FastQuickActionsWindow).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var createParams = (WF.CreateParams)property.GetValue(overlay)!;

            Assert.NotEqual(0, createParams.ClassStyle & 0x00020000);
        });
    }

    [Fact]
    public void ExportSaveBehaviorWritesDirectlyToConfiguredFolder()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-qao-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            RunSta(() =>
            {
                using var bitmap = new SD.Bitmap(1200, 760);
                var settings = new SettingsService();
                settings.Current.SaveFolder = directory;
                settings.Current.ImageFormat = "png";
                settings.Current.FileNameTemplate = "Quick Access Test";
                settings.Current.OverlaySaveButtonBehavior = "export";
                using var overlay = new FastQuickActionsWindow(bitmap, settings);
                var method = typeof(FastQuickActionsWindow).GetMethod("SaveCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

                var task = (Task)method.Invoke(overlay, new object?[] { false, false, null, true })!;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    WF.Application.DoEvents();
                    Thread.Sleep(10);
                }
                task.GetAwaiter().GetResult();

                Assert.Single(Directory.GetFiles(directory, "*.png"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ABatchSaveSaysHowManyLandedAndNamesTheFolder()
    {
        string folder = Path.Combine("C:", "Users", "someone", "Captures");

        (string title, string body) = FastQuickActionsWindow.BatchSaveConfirmation(4, 0, folder);
        Assert.Equal("Saved 4 captures", title);
        // A sentence, like every other toast body the card raises — not a bare folder name.
        Assert.Equal("All of them are in Captures.", body);

        (string one, string oneBody) = FastQuickActionsWindow.BatchSaveConfirmation(1, 0, folder);
        Assert.Equal("Saved 1 capture", one);
        Assert.Equal("It is in Captures.", oneBody);
    }

    [Fact]
    public void ABatchSaveThatPartlyFailedNeverReportsPlainSuccess()
    {
        string folder = Path.Combine("C:", "Users", "someone", "Captures");

        // The whole reason the batch counts instead of toasting per card: five per-card
        // toasts replaced each other and a failure was invisible behind the last one.
        (string partial, string partialBody) = FastQuickActionsWindow.BatchSaveConfirmation(3, 2, folder);
        Assert.Equal("Saved 3 of 5 captures", partial);
        Assert.Contains("2 could not be written", partialBody);

        (string none, string noneBody) = FastQuickActionsWindow.BatchSaveConfirmation(0, 2, folder);
        Assert.Equal("Nothing was saved", none);
        Assert.Contains("Captures", noneBody);
    }

    [Fact]
    public void TheCardHasItsThumbnailBeforeItIsEverShown()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using (var pen = new SD.Pen(SD.Color.Orange, 40))
            using (var g = SD.Graphics.FromImage(bitmap))
            {
                g.Clear(SD.Color.MidnightBlue);
                g.DrawLine(pen, 0, 0, 1200, 760);
            }

            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var preview = (SD.Bitmap?)typeof(FastQuickActionsWindow)
                .GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay);

            // Not merely present: the real capture's pixels, at the thumbnail's own size.
            Assert.NotNull(preview);
            Assert.Equal(overlay.ClientSize, preview!.Size);
            Assert.NotEqual(SD.Color.Empty.ToArgb(), preview.GetPixel(preview.Width / 2, preview.Height / 2).ToArgb());
        });
    }

    [Fact]
    public void TheCardSlidesInFromOffScreenAndLandsExactlyOnItsSlot()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            // Pin the monitor: "move to the active screen" reads the live cursor, which on a
            // multi-monitor machine can answer differently between two placements and make
            // the settled slot itself move.
            settings.Current.OverlayMoveToActiveScreen = false;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);

            overlay.Show();
            // The move happens before the first paint, so by the time Show returns the card
            // is already off its slot rather than sitting on it.
            SD.Point duringEntrance = overlay.Location;

            PumpUntil(() => Motion.Holders == 0, 4000);
            SD.Point settled = overlay.Location;

            Assert.NotEqual(settled, duringEntrance);
            Assert.Equal(settled.X, duringEntrance.X);
            Assert.True(duringEntrance.Y > settled.Y, "a bottom-anchored card comes up from below its slot");
            Assert.True(
                settled.Y + overlay.Height <= WF.Screen.FromRectangle(overlay.Bounds).WorkingArea.Bottom,
                "the card settles fully inside the working area");
            Assert.Equal(0, Motion.Holders);
            overlay.Hide();
        });
    }

    [Fact]
    public void ClosingMidEntranceReleasesTheHighResolutionClock()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            overlay.Show();
            overlay.Dispose();

            PumpUntil(() => Motion.Holders == 0, 4000);
            Assert.Equal(0, Motion.Holders);
        });
    }

    [Fact]
    public void HoverChromeCrossFadesRatherThanSnappingOn()
    {
        RunSta(() =>
        {
            using var bitmap = CreateBusyCapture();
            var settings = new SettingsService();
            using var overlay = FastQuickActionsWindow.CreateForTheme(bitmap, settings, QuickAccessOverlayTheme.Dark);
            overlay.CreateControl();
            InstallPreviews(overlay, bitmap);

            double idle = MeanLuminance(RenderCard(overlay));
            SetHoverAmount(overlay, 1d);
            double full = MeanLuminance(RenderCard(overlay));
            SetHoverAmount(overlay, 0.5d);
            double half = MeanLuminance(RenderCard(overlay));

            // Not eyeballed: the mid-fade frame has to actually sit between the two ends.
            Assert.NotEqual(idle, full, 3);
            double low = Math.Min(idle, full);
            double high = Math.Max(idle, full);
            Assert.InRange(half, low + 0.01, high - 0.01);
        });
    }

    [Fact]
    public void AHiddenCardJumpsStraightToTheHoverEndStateAndLeaksNoClock()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            var amount = typeof(FastQuickActionsWindow)
                .GetField("_hoverAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Invoke(overlay, "SetHovering", true);
            Assert.Equal(1d, (double)amount.GetValue(overlay)!, 6);
            Invoke(overlay, "SetHovering", false);
            Assert.Equal(0d, (double)amount.GetValue(overlay)!, 6);
            Assert.Equal(0, Motion.Holders);
        });
    }

    [Fact]
    public void AShownCardRampsItsHoverAndReleasesTheClockWhenItLands()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            overlay.Show();
            PumpUntil(() => Motion.Holders == 0, 4000);

            var amount = typeof(FastQuickActionsWindow)
                .GetField("_hoverAmount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Invoke(overlay, "SetHovering", true);

            // Still on its way, not already there — that is what makes it a cross-fade.
            Assert.True((double)amount.GetValue(overlay)! < 1d);
            PumpUntil(() => Motion.Holders == 0, 4000);

            Assert.Equal(1d, (double)amount.GetValue(overlay)!, 6);
            Assert.Equal(0, Motion.Holders);
            overlay.Hide();
        });
    }

    private static void SetHoverAmount(FastQuickActionsWindow overlay, double amount)
        => typeof(FastQuickActionsWindow)
            .GetField("_hoverAmount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(overlay, amount);

    private static void InstallPreviews(FastQuickActionsWindow overlay, SD.Bitmap source)
    {
        var type = typeof(FastQuickActionsWindow);
        var thumb = (SD.Rectangle)type.GetField("_thumbRect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay)!;
        var preview = (SD.Bitmap)type.GetMethod("CreatePreviewBitmap", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { source, thumb.Width, thumb.Height, false })!;
        var blurred = (SD.Bitmap)type.GetMethod("CreateBlurred", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { preview })!;
        type.GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, preview);
        type.GetField("_blurredPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, blurred);
    }

    private static SD.Bitmap RenderCard(FastQuickActionsWindow overlay)
    {
        var bitmap = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        using var args = new WF.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastQuickActionsWindow)
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, new object[] { args });
        return bitmap;
    }

    private static double MeanLuminance(SD.Bitmap bitmap)
    {
        using (bitmap)
        {
            double total = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    SD.Color pixel = bitmap.GetPixel(x, y);
                    total += (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) / 255d;
                }
            }
            return total / (bitmap.Width * bitmap.Height);
        }
    }

    private static SD.Bitmap CreateBusyCapture()
    {
        var bitmap = new SD.Bitmap(1200, 760);
        using var g = SD.Graphics.FromImage(bitmap);
        g.Clear(SD.Color.FromArgb(22, 41, 72));
        using var pen = new SD.Pen(SD.Color.FromArgb(240, 244, 250), 24);
        for (int x = -800; x < 1200; x += 90)
            g.DrawLine(pen, x, 0, x + 800, 760);
        return bitmap;
    }

    [Fact]
    public void TheEditorRoundTripKeepsTheCardAndUpdatesItInPlace()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            settings.Current.OverlayAutoClose = true;
            settings.Current.OverlayAutoCloseSeconds = 20;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            overlay.Show();
            PumpUntil(() => Motion.Holders == 0, 4000);
            var timer = (WF.Timer)typeof(FastQuickActionsWindow)
                .GetField("_autoCloseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(overlay)!;
            var versionField = typeof(FastQuickActionsWindow)
                .GetField("_imageVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;

            overlay.StepAside();
            // The card dims out over QuickMs rather than cutting, so wait the fade out.
            PumpUntil(() => !overlay.Visible, 3000);

            // Out of the way, not gone — and it must not auto-close under the editor.
            Assert.False(overlay.Visible);
            Assert.False(overlay.IsDisposed);
            Assert.False(timer.Enabled);

            overlay.ReplaceImageFromEditor(new SD.Bitmap(640, 400));
            PumpUntilRenderIdle(overlay);
            overlay.ComeBack();

            using var annotated = overlay.CloneImage();
            Assert.Equal(new SD.Size(640, 400), annotated.Size);
            Assert.Equal(1, (int)versionField.GetValue(overlay)!);
            Assert.True(overlay.Visible);
            Assert.True(timer.Enabled);

            PumpUntil(() => Motion.Holders == 0, 4000);
            Assert.Equal(0, Motion.Holders);
            overlay.Hide();
        });
    }

    /// <summary>
    /// The annotate round-trip when the editor closes inside the 140 ms step-aside fade —
    /// Escape buffered while it initialises, or an editor that dismisses itself. ComeBack
    /// used to see a card that was still Visible, treat it as "nothing to do" and return
    /// before clearing _offScreen or cancelling the fade; the fade then hid the card and
    /// nothing revived it, because only _temporarilyHidden cards are revived by the next
    /// capture. The capture vanished from the corner for the life of the process.
    /// </summary>
    [Fact]
    public void AnEditorThatClosesInsideTheStepAsideFadeStillGivesTheCardBack()
    {
        RunSta(() =>
        {
            Func<bool> previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => true;
            var made = new List<FastQuickActionsWindow>();
            try
            {
                var settings = new SettingsService();
                settings.Current.OverlayMoveToActiveScreen = false;
                var card = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                made.Add(card);
                card.Show();
                PumpUntil(() => Motion.Holders == 0, 4000);

                card.StepAside();            // App.AnnotateFromOverlay
                WF.Application.DoEvents();   // one frame of the fade
                card.ComeBack();             // the editor's Closed handler
                PumpUntil(() => Motion.Holders == 0, 4000);

                Assert.True(card.Visible, "the annotated card never came back");
                Assert.Equal(1d, card.Opacity, 2);

                // And it is back in the stack, so the next capture stacks above it rather
                // than landing on top of it.
                var next = new FastQuickActionsWindow(new SD.Bitmap(600, 400), settings);
                made.Add(next);
                next.Show();
                PumpUntil(() => Motion.Holders == 0, 4000);
                Assert.NotEqual(card.Bounds, next.Bounds);
            }
            finally
            {
                Motion.AnimationsEnabled = previous;
                foreach (FastQuickActionsWindow card in made)
                    card.Dispose();
                WF.Application.DoEvents();
            }
        });
    }

    /// <summary>
    /// Hover alone must not outrank the window the user is typing in. The corner is a normal
    /// place to park a pointer and the card does not auto-close by default, so a hovered card
    /// used to eat Ctrl+S out of a document — silently, for as long as it sat there.
    /// </summary>
    [Fact]
    public void AParkedPointerStopsTheCardTakingKeysOffTheForegroundApp()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            overlay.Show();

            // Never hovered: nothing to claim.
            Assert.False((bool)InvokeWithResult(overlay, "ShortcutsAreArmed")!);

            // The pointer arrives — the card may claim its four chords.
            Invoke(overlay, "SetHovering", true);
            Assert.True((bool)InvokeWithResult(overlay, "ShortcutsAreArmed")!);

            // Left sitting there, it goes back to being just a pointer.
            SetPrivate(overlay, "_shortcutArmedAt", Environment.TickCount64 - 10_000);
            Assert.False((bool)InvokeWithResult(overlay, "ShortcutsAreArmed")!);

            // And focus moving while the pointer stays put — the Alt+Tab-then-Ctrl+S case —
            // disarms it whatever the clock says.
            Invoke(overlay, "ArmShortcuts");
            Assert.True((bool)InvokeWithResult(overlay, "ShortcutsAreArmed")!);
            SetPrivate(overlay, "_shortcutForeground", new IntPtr(0x1234));
            Assert.False((bool)InvokeWithResult(overlay, "ShortcutsAreArmed")!);

            PumpUntil(() => Motion.Holders == 0, 4000);
        });
    }

    [Fact]
    public void TheShortcutHookLivesExactlyAsLongAsTheHover()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            using var overlay = new FastQuickActionsWindow(bitmap, settings);
            var hook = typeof(FastQuickActionsWindow)
                .GetField("_keyHook", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Off-screen the card must never take a keystroke off the app underneath.
            Invoke(overlay, "SetHovering", true);
            Assert.Equal(IntPtr.Zero, (IntPtr)hook.GetValue(overlay)!);

            overlay.Show();
            Invoke(overlay, "SetHovering", true);
            Assert.NotEqual(IntPtr.Zero, (IntPtr)hook.GetValue(overlay)!);

            Invoke(overlay, "SetHovering", false);
            Assert.Equal(IntPtr.Zero, (IntPtr)hook.GetValue(overlay)!);

            Invoke(overlay, "SetHovering", true);
            overlay.StepAside();
            Assert.Equal(IntPtr.Zero, (IntPtr)hook.GetValue(overlay)!);

            PumpUntil(() => Motion.Holders == 0, 4000);
        });
    }

    [Fact]
    public void ABurstOfCapturesDismissesTheOldestCardsInsteadOfPilingUp()
    {
        RunSta(() =>
        {
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            var cards = new List<FastQuickActionsWindow>();
            try
            {
                for (int i = 0; i < 9; i++)
                {
                    using var bitmap = new SD.Bitmap(1200, 760);
                    var card = new FastQuickActionsWindow(bitmap, settings);
                    cards.Add(card);
                    card.Show();
                }

                int capacity = QuickAccessOverlayLayout.StackCapacity(
                    WF.Screen.PrimaryScreen!.WorkingArea,
                    cards[^1].Size,
                    settings.Current.OverlayPosition,
                    cards[^1].DeviceDpi);
                int living = cards.Count(c => !c.IsDisposed && !IsLeaving(c));

                Assert.InRange(capacity, 1, 5);
                Assert.Equal(capacity, living);
                // Oldest first: the survivors are the tail of the burst.
                Assert.All(cards.TakeLast(capacity), c => Assert.False(IsLeaving(c)));
                Assert.All(cards.Take(cards.Count - capacity), c => Assert.True(IsLeaving(c)));
            }
            finally
            {
                foreach (var card in cards)
                {
                    AllowImmediateClose(card);
                    card.Dispose();
                }
                PumpUntil(() => Motion.Holders == 0, 4000);
            }
        });
    }

    private static bool IsLeaving(FastQuickActionsWindow card)
        => (bool)typeof(FastQuickActionsWindow)
            .GetField("_exitMotionStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(card)!;

    private static void AllowImmediateClose(FastQuickActionsWindow card)
        => typeof(FastQuickActionsWindow)
            .GetField("_allowImmediateClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(card, true);

    [Fact]
    public void TemporarilyHideDiscardsNothingAndTheNextCaptureBringsTheStackBack()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-qao-hide-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string capture = Path.Combine(directory, "capture.png");
        using (var written = new SD.Bitmap(16, 16))
            written.Save(capture, SD.Imaging.ImageFormat.Png);

        RunSta(() =>
        {
            // Drain anything an earlier test left on the recently-closed stack.
            while (FastQuickActionsWindow.PopRecentlyClosed() is not null) { }

            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            settings.Current.OverlayAutoClose = true;
            settings.Current.OverlayAutoCloseSeconds = 20;
            using var bitmap = new SD.Bitmap(1200, 760);
            using var first = new FastQuickActionsWindow(bitmap, settings, capture);
            using var second = new FastQuickActionsWindow(bitmap, settings);
            first.Show();
            second.Show();

            Invoke(second, "RunMenuAction", QuickActionsMenu.TemporarilyHide, "Temporarily hide");
            PumpUntil(() => !first.Visible && !second.Visible, 3000);

            Assert.False(first.Visible);
            Assert.False(second.Visible);
            Assert.False(first.IsDisposed);
            Assert.False(second.IsDisposed);
            // Non-destructive: nothing auto-closed, nothing went to recently-closed.
            Assert.Null(FastQuickActionsWindow.PopRecentlyClosed());

            using var third = new FastQuickActionsWindow(bitmap, settings);
            Assert.True(first.Visible);
            Assert.True(second.Visible);

            third.Dispose();
            PumpUntil(() => Motion.Holders == 0, 4000);
        });

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void DraggingOutOffersTheFileAndTheBitmap()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(64, 40);
            WF.DataObject data = FastQuickActionsWindow.CreateDragData(@"C:\captures\shot.png", bitmap);

            // The file is what mail clients attach; the bitmap is what canvas and chat
            // apps take, and without it they refused the drop outright.
            Assert.True(data.GetDataPresent(WF.DataFormats.FileDrop));
            Assert.True(data.GetDataPresent(WF.DataFormats.Bitmap));
            Assert.Equal(new[] { @"C:\captures\shot.png" }, (string[])data.GetData(WF.DataFormats.FileDrop)!);
        });
    }

    [Fact]
    public void SavingSaysWhichFileItWrote()
    {
        (string title, string body) = FastQuickActionsWindow.SaveConfirmation(
            Path.Combine("C:", "Users", "someone", "Pictures", "WinShot 2026-09-04 at 11.02.14.png"));

        // Sentence case, and the file rather than the whole path — the card closes on save,
        // so this line is the only thing that says the capture went anywhere.
        Assert.Equal("Saved", title);
        Assert.Equal("WinShot 2026-09-04 at 11.02.14.png", body);
    }

    [Fact]
    public void DoubleClickingTheThumbnailOpensTheEditorAndASingleClickDoesNot()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            int edits = 0;
            overlay.EditRequested += _ => edits++;
            var body = new SD.Point(overlay.ClientSize.Width / 2, overlay.ClientSize.Height / 4);

            Click(overlay, body, clicks: 1);
            Assert.Equal(0, edits);

            Click(overlay, body, clicks: 2);
            Assert.Equal(1, edits);
        });
    }

    [Fact]
    public void ADoubleClickThatStartedAsADragDoesNotOpenTheEditor()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            using var overlay = new FastQuickActionsWindow(bitmap, new SettingsService());
            int edits = 0;
            overlay.EditRequested += _ => edits++;
            var body = new SD.Point(overlay.ClientSize.Width / 2, overlay.ClientSize.Height / 4);

            // A release with nothing armed is what a finished drag looks like.
            Invoke(overlay, "OnOverlayMouseUp", overlay, MouseArgs(body, clicks: 2));

            Assert.Equal(0, edits);
        });
    }

    private static void Click(FastQuickActionsWindow overlay, SD.Point at, int clicks)
    {
        Invoke(overlay, "OnOverlayMouseDown", overlay, MouseArgs(at, 1));
        Invoke(overlay, "OnOverlayMouseUp", overlay, MouseArgs(at, clicks));
    }

    private static WF.MouseEventArgs MouseArgs(SD.Point at, int clicks)
        => new(WF.MouseButtons.Left, clicks, at.X, at.Y, 0);

    [Fact]
    public void ARecordingCardDiffersFromAScreenshotCardOnlyInsideItsStrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"winshot-qao-strip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string media = Path.Combine(directory, "recording.mp4");
        File.WriteAllBytes(media, new byte[86016]);

        try
        {
            RunSta(() =>
            {
                // Each card takes ownership of the bitmap it is handed, so the two get
                // their own copy of the same pixels.
                var settings = new SettingsService();
                using var shot = FastQuickActionsWindow.CreateForTheme(
                    CreateBusyCapture(), settings, QuickAccessOverlayTheme.Dark);
                shot.CreateControl();
                using (var source = CreateBusyCapture())
                    InstallPreviews(shot, source);
                using SD.Bitmap plain = RenderCard(shot);

                using var recording = FastQuickActionsWindow.CreateForMediaFile(
                    media, CreateBusyCapture(), settings, canEdit: true, duration: TimeSpan.FromSeconds(53));
                recording.CreateControl();
                using (var source = CreateBusyCapture())
                    InstallPreviews(recording, source);
                using SD.Bitmap withStrip = RenderCard(recording);

                Assert.Equal(plain.Size, withStrip.Size);
                var card = (SD.Rectangle)typeof(FastQuickActionsWindow)
                    .GetField("_cardRect", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(recording)!;
                SD.Rectangle band = MediaCardStrip.GradientBounds(card, recording.DeviceDpi);

                // The card is pinned: everything above the strip has to be the same bytes.
                for (int y = 0; y < band.Top; y++)
                {
                    for (int x = 0; x < plain.Width; x++)
                        Assert.Equal(plain.GetPixel(x, y).ToArgb(), withStrip.GetPixel(x, y).ToArgb());
                }

                // And the strip has to actually be there — scanned, not eyeballed.
                int changed = 0;
                for (int y = band.Top; y < band.Bottom; y++)
                {
                    for (int x = 0; x < plain.Width; x++)
                    {
                        if (plain.GetPixel(x, y).ToArgb() != withStrip.GetPixel(x, y).ToArgb())
                            changed++;
                    }
                }
                Assert.True(changed > band.Width, $"the recording strip barely drew anything ({changed} pixels)");
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void APinGrowsOutOfTheCardItCameFrom()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            using var card = new FastQuickActionsWindow(bitmap, settings);
            card.CreateControl();
            SD.Rectangle origin = card.CardScreenBounds;

            using var pin = new WinShot.Pin.FastPinWindow(card.CloneImage(), settings);
            pin.CreateControl();
            SD.Rectangle settled = pin.Bounds;
            pin.GrowFrom(origin);

            pin.Show();
            Assert.Equal(origin, pin.Bounds);

            PumpUntil(() => Motion.Holders == 0 && pin.Bounds == settled, 4000);

            Assert.Equal(settled, pin.Bounds);
            Assert.Equal(0, Motion.Holders);
            pin.Hide();
        });
    }

    [Fact]
    public void ClosingTheCardForAPinSkipsTheExitFlight()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            settings.Current.OverlayMoveToActiveScreen = false;
            var card = new FastQuickActionsWindow(bitmap, settings);
            card.Show();
            PumpUntil(() => Motion.Holders == 0, 4000);

            card.CloseWithoutMotion();

            // Two copies of the same capture, one growing and one flying off, reads as a
            // duplicate rather than as one object moving.
            Assert.False(IsLeaving(card));
            Assert.True(card.IsDisposed);
            PumpUntil(() => Motion.Holders == 0, 4000);
            Assert.Equal(0, Motion.Holders);
        });
    }

    /// <summary>
    /// The re-aim in PositionFromSettings has to fire while an entrance is in flight and
    /// never after one has landed. It reads a flag rather than the Motion handle because
    /// with animation turned off Motion.Run applies the end state and calls onDone INLINE,
    /// before the caller ever gets a handle to assign — so the handle field is non-null for
    /// a tween that already finished, and a handle-based guard replayed the whole entrance
    /// on every later reposition: the card thrown off screen and snapped back, on a machine
    /// whose owner asked for no animation at all.
    /// </summary>
    [Fact]
    public void ARepositionReAimsAnEntranceInFlightButNeverReplaysOneThatLanded()
    {
        RunSta(() =>
        {
            using var bitmap = new SD.Bitmap(1200, 760);
            var settings = new SettingsService();
            // Pin the monitor: "move to the active screen" reads the live cursor, so a second
            // placement could otherwise choose a different slot for reasons of its own.
            settings.Current.OverlayMoveToActiveScreen = false;

            using (new AnimationSetting(false))
            {
                using var landed = new FastQuickActionsWindow(bitmap, settings);
                landed.Show();

                // The trap itself, asserted: the tween is over, and its handle is not null.
                Assert.NotNull(EnterMotion(landed));
                Assert.False(EnterMotionRunning(landed));

                SD.Rectangle area = WF.Screen.FromRectangle(landed.Bounds).WorkingArea;
                var moves = new List<SD.Rectangle>();
                void Track(object? sender, EventArgs e) => moves.Add(landed.Bounds);
                landed.LocationChanged += Track;
                Invoke(landed, "PositionFromSettings");
                landed.LocationChanged -= Track;

                // The settled location alone proves nothing — a replay ends where it began.
                // Watch the way through: the entrance starts from outside the anchored edge,
                // so a replayed one is the only thing that puts the card off the working area.
                // (Not an equality against one slot: OpenWindows is static and another test
                // class's card landing mid-run legitimately restacks this one.)
                Assert.All(moves, bounds => Assert.True(
                    area.Contains(bounds),
                    $"a landed card must not leave the working area on a reposition, but went to {bounds}"));
            }

            using (new AnimationSetting(true))
            {
                using var flying = new FastQuickActionsWindow(bitmap, settings);
                flying.Show();

                // The other half of the guard: a card still on its way is re-aimed, or
                // learning its monitor's DPI mid-slide drops it on the stale slot and eats
                // the entrance. A fresh tween handle is that re-aim.
                Assert.True(EnterMotionRunning(flying));
                object? before = EnterMotion(flying);
                Invoke(flying, "PositionFromSettings");
                Assert.NotSame(before, EnterMotion(flying));
                flying.Hide();
            }
        });
    }

    private static object? EnterMotion(FastQuickActionsWindow overlay)
        => typeof(FastQuickActionsWindow)
            .GetField("_enterMotion", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay);

    private static bool EnterMotionRunning(FastQuickActionsWindow overlay)
        => (bool)typeof(FastQuickActionsWindow)
            .GetField("_enterMotionRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay)!;

    private sealed class AnimationSetting : IDisposable
    {
        private readonly Func<bool> _previous;

        public AnimationSetting(bool enabled)
        {
            _previous = Motion.AnimationsEnabled;
            Motion.AnimationsEnabled = () => enabled;
        }

        public void Dispose() => Motion.AnimationsEnabled = _previous;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            WF.Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void InvokeKey(FastQuickActionsWindow overlay, WF.Keys keys)
        => Invoke(overlay, "OnOverlayKeyDown", overlay, new WF.KeyEventArgs(keys));

    private static object? InvokeWithResult(FastQuickActionsWindow overlay, string method, params object[] args)
        => typeof(FastQuickActionsWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, args);

    private static void SetPrivate(FastQuickActionsWindow overlay, string field, object value)
        => typeof(FastQuickActionsWindow)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(overlay, value);

    private static void Invoke(FastQuickActionsWindow overlay, string method, params object[] args)
        => typeof(FastQuickActionsWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, args);

    private static List<object> GetButtons(FastQuickActionsWindow overlay)
    {
        var field = typeof(FastQuickActionsWindow).GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.IEnumerable)field.GetValue(overlay)!).Cast<object>().ToList();
    }

    private static ButtonSnapshot Button(IEnumerable<object> buttons, string name)
    {
        object button = buttons.Single(item =>
            string.Equals(
                item.GetType().GetProperty("AccessibleName")!.GetValue(item) as string,
                name,
                StringComparison.Ordinal));
        return new ButtonSnapshot(
            (SD.Rectangle)button.GetType().GetProperty("Bounds")!.GetValue(button)!,
            button.GetType().GetProperty("Icon")!.GetValue(button) is SD.Bitmap,
            (button.GetType().GetProperty("Icon")!.GetValue(button) as SD.Bitmap)?.Size,
            (button.GetType().GetProperty("LabelFont")!.GetValue(button) as SD.Font)?.SizeInPoints);
    }

    private readonly record struct ButtonSnapshot(SD.Rectangle Bounds, bool IconLoaded, SD.Size? IconSize, float? LabelFontSize);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
