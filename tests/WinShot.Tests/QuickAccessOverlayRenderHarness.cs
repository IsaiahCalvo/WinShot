using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// Gated, in-process visual evidence for the post-capture overlay. Uses synthetic artwork
/// and DrawToBitmap, so it never launches or controls the installed WinShot application.
/// </summary>
public class QuickAccessOverlayRenderHarness
{
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
                var settings = new SettingsService();
                RenderState(source, settings, hovering: false, Path.Combine(outDir, "idle.png"));
                RenderState(source, settings, hovering: true, Path.Combine(outDir, "hover.png"));
                RenderSaveIcon(Path.Combine(outDir, "save-icon-source-render.png"));
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
        Assert.True(File.Exists(Path.Combine(outDir, "idle.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "hover.png")));
    }

    private static void RenderSaveIcon(string path)
    {
        var method = typeof(FastQuickActionsWindow)
            .GetMethod("CreateSaveIcon", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var icon = (SD.Bitmap)method.Invoke(null, new object[] { 100 })!;
        icon.Save(path, SD.Imaging.ImageFormat.Png);
    }

    private static void RenderState(SD.Bitmap source, SettingsService settings, bool hovering, string path)
    {
        using var overlay = new FastQuickActionsWindow(source, settings);
        overlay.CreateControl();
        SetPreviewBitmaps(overlay, source);
        if (hovering)
            Invoke(overlay, "SetHovering", true);
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
        using var bitmap = new SD.Bitmap(overlay.ClientSize.Width, overlay.ClientSize.Height);
        using var graphics = SD.Graphics.FromImage(bitmap);
        using var args = new System.Windows.Forms.PaintEventArgs(graphics, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        typeof(FastQuickActionsWindow)
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(overlay, new object[] { args });
        bitmap.Save(path, SD.Imaging.ImageFormat.Png);
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
