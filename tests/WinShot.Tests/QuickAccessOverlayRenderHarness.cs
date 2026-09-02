using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// Gated, in-process visual evidence for the post-capture overlay. Uses synthetic artwork
/// and DrawToBitmap, so it never launches or controls the installed WinShot application.
/// </summary>
public class QuickAccessOverlayRenderHarness
{
    [Fact]
    public void RenderContextMenuToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_QUICK_ACCESS") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-quick-access-render");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = CreateSyntheticCapture();
                using var overlay = new FastQuickActionsWindow(source, new SettingsService());
                var menu = (WF.ContextMenuStrip)typeof(FastQuickActionsWindow)
                    .GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(overlay)!;

                menu.Show(new SD.Point(0, 0));
                using var bitmap = new SD.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bitmap, new SD.Rectangle(0, 0, menu.Width, menu.Height));
                bitmap.Save(Path.Combine(outDir, "context-menu.png"), SD.Imaging.ImageFormat.Png);

                // Hover state: the highlight has to inset evenly from both menu edges.
                menu.Items.OfType<QuickActionMenuItem>().Skip(5).First().Select();
                menu.Refresh();
                using var hovered = new SD.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(hovered, new SD.Rectangle(0, 0, menu.Width, menu.Height));
                hovered.Save(Path.Combine(outDir, "context-menu-hover.png"), SD.Imaging.ImageFormat.Png);

                // The Share submenu, when this machine has Blip and the row expands.
                foreach (WF.ToolStripItem item in menu.Items)
                {
                    if (item is not WF.ToolStripMenuItem { HasDropDownItems: true } parent)
                        continue;
                    parent.DropDown.Show(new SD.Point(0, 0));
                    using var sub = new SD.Bitmap(parent.DropDown.Width, parent.DropDown.Height);
                    parent.DropDown.DrawToBitmap(sub, new SD.Rectangle(0, 0, sub.Width, sub.Height));
                    parent.DropDown.Close();
                    sub.Save(Path.Combine(outDir, $"context-submenu-{item.Text}.png"), SD.Imaging.ImageFormat.Png);
                }
                menu.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void RenderIdleAndHoverStatesToPng()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_QUICK_ACCESS") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-quick-access-render");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = CreateSyntheticCapture();
                using var wide = CreateAspectCapture(2400, 400, SD.Color.FromArgb(48, 84, 150));
                using var tall = CreateAspectCapture(400, 2400, SD.Color.FromArgb(100, 61, 142));
                var settings = new SettingsService();
                RenderTheme(source, settings, QuickAccessOverlayTheme.Dark, "dark", outDir);
                RenderTheme(source, settings, QuickAccessOverlayTheme.Light, "light", outDir);
                RenderState(wide, settings, QuickAccessOverlayTheme.Dark, hovering: false, hoverButton: -1, Path.Combine(outDir, "dark-wide-idle.png"));
                RenderState(tall, settings, QuickAccessOverlayTheme.Dark, hovering: false, hoverButton: -1, Path.Combine(outDir, "dark-tall-idle.png"));
                RenderStackMotion(source, settings, outDir);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(outDir, "dark-idle.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "dark-hover.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "light-idle.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "light-hover.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "dark-tooltip-pin.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "light-tooltip-save.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "dark-stack-motion-contact-sheet.png")));
        using var wideRender = SD.Image.FromFile(Path.Combine(outDir, "dark-wide-idle.png"));
        using var tallRender = SD.Image.FromFile(Path.Combine(outDir, "dark-tall-idle.png"));
        Assert.Equal(new SD.Size(192, 120), wideRender.Size);
        Assert.Equal(wideRender.Size, tallRender.Size);
    }

    private static void RenderTheme(
        SD.Bitmap source,
        SettingsService settings,
        QuickAccessOverlayTheme theme,
        string prefix,
        string outDir)
    {
        RenderState(source, settings, theme, hovering: false, hoverButton: -1, Path.Combine(outDir, $"{prefix}-idle.png"));
        RenderState(source, settings, theme, hovering: true, hoverButton: -1, Path.Combine(outDir, $"{prefix}-hover.png"));
        RenderState(source, settings, theme, hovering: true, hoverButton: 3, Path.Combine(outDir, $"{prefix}-hover-save.png"));
        RenderState(source, settings, theme, hovering: true, hoverButton: 4, Path.Combine(outDir, $"{prefix}-hover-copy.png"));
        int tooltipButton = theme == QuickAccessOverlayTheme.Dark ? 0 : 3;
        string tooltipText = theme == QuickAccessOverlayTheme.Dark ? "Pin" : "Save";
        RenderStateWithTooltip(
            source,
            settings,
            theme,
            tooltipButton,
            tooltipText,
            Path.Combine(outDir, $"{prefix}-tooltip-{tooltipText.ToLowerInvariant()}.png"));
    }

    private static void RenderState(
        SD.Bitmap source,
        SettingsService settings,
        QuickAccessOverlayTheme theme,
        bool hovering,
        int hoverButton,
        string path)
    {
        using var overlay = FastQuickActionsWindow.CreateForTheme(source, settings, theme);
        overlay.CreateControl();
        SetPreviewBitmaps(overlay, source);
        if (hovering)
            Invoke(overlay, "SetHovering", true);
        if (hoverButton >= 0)
            Invoke(overlay, "SetHover", hoverButton);
        Render(overlay, path);
    }

    private static void SetPreviewBitmaps(FastQuickActionsWindow overlay, SD.Bitmap source)
    {
        var type = typeof(FastQuickActionsWindow);
        var thumb = (SD.Rectangle)type.GetField("_thumbRect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(overlay)!;
        var createPreview = type.GetMethod("CreatePreviewBitmap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var createBlurred = type.GetMethod("CreateBlurred", BindingFlags.Static | BindingFlags.NonPublic)!;
        var preview = (SD.Bitmap)createPreview.Invoke(null, new object[] { source, thumb.Width, thumb.Height })!;
        var blurred = (SD.Bitmap)createBlurred.Invoke(null, new object[] { preview })!;
        type.GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, preview);
        type.GetField("_blurredPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, blurred);
    }

    private static void Invoke(FastQuickActionsWindow overlay, string name, params object[] args)
        => typeof(FastQuickActionsWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, args);

    private static void Render(FastQuickActionsWindow overlay, string path)
    {
        using var bitmap = RenderBitmap(overlay);
        bitmap.Save(path, SD.Imaging.ImageFormat.Png);
    }

    private static SD.Bitmap RenderBitmap(FastQuickActionsWindow overlay)
    {
        var bitmap = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        using var args = new System.Windows.Forms.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastQuickActionsWindow)
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, new object[] { args });
        return bitmap;
    }

    private static void RenderStateWithTooltip(
        SD.Bitmap source,
        SettingsService settings,
        QuickAccessOverlayTheme theme,
        int hoverButton,
        string tooltipText,
        string path)
    {
        using var overlay = FastQuickActionsWindow.CreateForTheme(source, settings, theme);
        overlay.CreateControl();
        SetPreviewBitmaps(overlay, source);
        Invoke(overlay, "SetHovering", true);
        Invoke(overlay, "SetHover", hoverButton);
        using var card = RenderBitmap(overlay);
        using var tooltip = QuickAccessTooltipRenderer.RenderEvidence(
            tooltipText,
            QuickAccessOverlayThemePalette.For(theme));

        const int inset = 16;
        var canvasSize = new SD.Size(card.Width + inset * 2, card.Height + inset * 2);
        using var canvas = new SD.Bitmap(canvasSize.Width, canvasSize.Height);
        using var graphics = SD.Graphics.FromImage(canvas);
        graphics.Clear(theme == QuickAccessOverlayTheme.Dark
            ? SD.Color.FromArgb(26, 29, 36)
            : SD.Color.FromArgb(232, 237, 244));
        graphics.DrawImageUnscaled(card, inset, inset);

        var buttonField = typeof(FastQuickActionsWindow).GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object button = ((System.Collections.IEnumerable)buttonField.GetValue(overlay)!).Cast<object>().ElementAt(hoverButton);
        var buttonBounds = (SD.Rectangle)button.GetType().GetProperty("Bounds")!.GetValue(button)!;
        buttonBounds.Offset(inset, inset);
        var cardBounds = new SD.Rectangle(inset, inset, card.Width, card.Height);
        SD.Point tooltipPoint = QuickAccessTooltipLayout.Place(
            new SD.Rectangle(SD.Point.Empty, canvasSize),
            tooltip.Size,
            buttonBounds,
            cardBounds,
            96);
        graphics.DrawImageUnscaled(tooltip, tooltipPoint);
        canvas.Save(path, SD.Imaging.ImageFormat.Png);
    }

    private static void RenderStackMotion(SD.Bitmap source, SettingsService settings, string outDir)
    {
        using var overlay = FastQuickActionsWindow.CreateForTheme(source, settings, QuickAccessOverlayTheme.Dark);
        overlay.CreateControl();
        SetPreviewBitmaps(overlay, source);
        using var card = RenderBitmap(overlay);

        var canvasSize = new SD.Size(720, 520);
        int edge = 24;
        int step = card.Height + 12;
        var baseSlot = new SD.Point(canvasSize.Width - card.Width - edge, canvasSize.Height - card.Height - edge);
        var secondSlot = new SD.Point(baseSlot.X, baseSlot.Y - step);
        var thirdSlot = new SD.Point(baseSlot.X, baseSlot.Y - step * 2);
        SD.Point exitTarget = QuickAccessOverlayMotion.ExitTarget(
            new SD.Rectangle(SD.Point.Empty, canvasSize),
            new SD.Rectangle(baseSlot, card.Size),
            "bottom-right");

        var frames = new List<SD.Bitmap>();
        foreach (double progress in new[] { 0d, 0.25d, 0.5d, 0.75d, 1d })
        {
            var frame = CreateMotionCanvas(canvasSize);
            using var graphics = SD.Graphics.FromImage(frame);
            graphics.DrawImageUnscaled(card, secondSlot);
            graphics.DrawImageUnscaled(card, thirdSlot);
            if (progress < 1d)
            {
                SD.Point point = QuickAccessOverlayMotion.Interpolate(
                    baseSlot,
                    exitTarget,
                    QuickAccessOverlayMotion.EaseInOutSine(progress));
                DrawWithOpacity(graphics, card, point, QuickAccessOverlayMotion.ExitOpacity(progress));
            }
            DrawMotionLabel(graphics, $"Exit {progress:P0}");
            frames.Add(frame);
        }

        foreach (double progress in new[] { 0.5d, 1d })
        {
            double eased = QuickAccessOverlayMotion.EaseOutCubic(progress);
            var frame = CreateMotionCanvas(canvasSize);
            using var graphics = SD.Graphics.FromImage(frame);
            graphics.DrawImageUnscaled(card, QuickAccessOverlayMotion.Interpolate(secondSlot, baseSlot, eased));
            graphics.DrawImageUnscaled(card, QuickAccessOverlayMotion.Interpolate(thirdSlot, secondSlot, eased));
            DrawMotionLabel(graphics, $"Restack {progress:P0}");
            frames.Add(frame);
        }

        const int columns = 4;
        const int cellWidth = 360;
        const int cellHeight = 260;
        int rows = (int)Math.Ceiling(frames.Count / (double)columns);
        using var sheet = new SD.Bitmap(columns * cellWidth, rows * cellHeight);
        using (var graphics = SD.Graphics.FromImage(sheet))
        {
            graphics.Clear(SD.Color.FromArgb(18, 21, 27));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < frames.Count; i++)
            {
                int x = i % columns * cellWidth;
                int y = i / columns * cellHeight;
                graphics.DrawImage(frames[i], new SD.Rectangle(x, y, cellWidth, cellHeight));
            }
        }
        sheet.Save(Path.Combine(outDir, "dark-stack-motion-contact-sheet.png"), SD.Imaging.ImageFormat.Png);
        foreach (SD.Bitmap frame in frames)
            frame.Dispose();
    }

    private static SD.Bitmap CreateMotionCanvas(SD.Size size)
    {
        var bitmap = new SD.Bitmap(size.Width, size.Height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        using var gradient = new LinearGradientBrush(
            new SD.Rectangle(SD.Point.Empty, size),
            SD.Color.FromArgb(34, 45, 65),
            SD.Color.FromArgb(20, 74, 88),
            35f);
        graphics.FillRectangle(gradient, new SD.Rectangle(SD.Point.Empty, size));
        return bitmap;
    }

    private static void DrawWithOpacity(SD.Graphics graphics, SD.Bitmap bitmap, SD.Point point, double opacity)
    {
        using var attributes = new SD.Imaging.ImageAttributes();
        var matrix = new SD.Imaging.ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0d, 1d) };
        attributes.SetColorMatrix(matrix, SD.Imaging.ColorMatrixFlag.Default, SD.Imaging.ColorAdjustType.Bitmap);
        graphics.DrawImage(
            bitmap,
            new SD.Rectangle(point, bitmap.Size),
            0,
            0,
            bitmap.Width,
            bitmap.Height,
            SD.GraphicsUnit.Pixel,
            attributes);
    }

    private static void DrawMotionLabel(SD.Graphics graphics, string text)
    {
        using var font = new SD.Font("Segoe UI", 18, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        graphics.DrawString(text, font, SD.Brushes.White, 18, 16);
    }

    private static SD.Bitmap CreateSyntheticCapture()
    {
        var bitmap = new SD.Bitmap(1200, 760);
        using var g = SD.Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var gradient = new LinearGradientBrush(
            new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            SD.Color.FromArgb(22, 41, 72),
            SD.Color.FromArgb(36, 115, 122),
            25f);
        g.FillRectangle(gradient, 0, 0, bitmap.Width, bitmap.Height);
        using var panel = new SD.SolidBrush(SD.Color.FromArgb(215, 245, 247, 250));
        using (var panelPath = RoundedRectangle(new SD.Rectangle(110, 110, 980, 540), 42))
            g.FillPath(panel, panelPath);
        using var accent = new SD.SolidBrush(SD.Color.FromArgb(42, 126, 221));
        g.FillEllipse(accent, 180, 185, 150, 150);
        using var titleFont = new SD.Font("Segoe UI", 44, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        using var bodyFont = new SD.Font("Segoe UI", 27, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        g.DrawString("SANITIZED CAPTURE", titleFont, SD.Brushes.Black, 390, 205);
        g.DrawString("Synthetic overlay render evidence", bodyFont, SD.Brushes.DimGray, 390, 285);
        using var line = new SD.Pen(SD.Color.FromArgb(90, 105, 125), 18) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line, 190, 430, 960, 430);
        g.DrawLine(line, 190, 495, 780, 495);
        return bitmap;
    }

    private static SD.Bitmap CreateAspectCapture(int width, int height, SD.Color color)
    {
        var bitmap = new SD.Bitmap(width, height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        graphics.Clear(color);
        int shortEdge = Math.Min(width, height);
        int inset = Math.Max(8, shortEdge / 8);
        using var accent = new SD.Pen(SD.Color.FromArgb(230, 245, 249), Math.Max(4, shortEdge / 25));
        graphics.DrawRectangle(accent, inset, inset, Math.Max(1, width - inset * 2), Math.Max(1, height - inset * 2));
        return bitmap;
    }

    private static GraphicsPath RoundedRectangle(SD.Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
