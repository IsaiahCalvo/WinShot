using System.Reflection;
using System.IO;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

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
        using var preview = (SD.Bitmap)method.Invoke(null, new object[] { source, 192, 120 })!;

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
            // Cloud upload, Quick Look and Temporarily Hide are the CleanShot rows we skipped.
            Assert.DoesNotContain(labels, l => l.Contains("Cloud", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(labels, l => l.Contains("Quick Look", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(labels, l => l.Contains("Temporarily", StringComparison.OrdinalIgnoreCase));
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
                    .Invoke(overlay, new object[] { false, true });

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

                var task = (Task)method.Invoke(overlay, new object[] { false, false })!;
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

    private static void InvokeKey(FastQuickActionsWindow overlay, WF.Keys keys)
        => Invoke(overlay, "OnOverlayKeyDown", overlay, new WF.KeyEventArgs(keys));

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
