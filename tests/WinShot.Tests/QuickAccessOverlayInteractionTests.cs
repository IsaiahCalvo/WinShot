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

            run.Invoke(overlay, new object[] { "rotate-right" });
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
