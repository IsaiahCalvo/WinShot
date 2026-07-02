using System.Drawing.Drawing2D;
using System.IO;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.History;

public sealed class FastQuickPreviewWindow : WF.Form
{
    private static readonly string[] PreviewableExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private static readonly SD.Color Back = SD.Color.FromArgb(30, 30, 30);
    private static readonly SD.Color Border = SD.Color.FromArgb(54, 255, 255, 255);
    private static readonly SD.Color TextColor = SD.Color.White;
    private static readonly SD.Color MutedText = SD.Color.FromArgb(187, 187, 187);

    private readonly string _filePath;
    private readonly bool _looksLikeImage;
    private SD.Image? _image;
    private bool _closing;

    public FastQuickPreviewWindow(string filePath)
    {
        _filePath = filePath;
        _looksLikeImage = PreviewableExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
        _image = _looksLikeImage ? TryLoadImage(filePath) : null;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;

        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        ClientSize = CalculateSize();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode is WF.Keys.Space or WF.Keys.Escape)
                CloseOnce();
        };
        MouseDown += (_, _) => CloseOnce();
        Deactivate += (_, _) => CloseOnce();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        CenterOnScreen();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnFormClosing(WF.FormClosingEventArgs e)
    {
        _closing = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _image?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Back);

        var content = new SD.Rectangle(8, 8, Width - 16, Height - 16);
        if (_image is not null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_image, content);
        }
        else
        {
            DrawFallback(e.Graphics, content);
        }

        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private SD.Size CalculateSize()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;

        if (_image is not null)
        {
            return HistoryPreviewLayout.CalculateImageSize(
                new SD.Size(_image.Width, _image.Height),
                area.Size);
        }

        return HistoryPreviewLayout.FallbackSize;
    }

    private void DrawFallback(SD.Graphics g, SD.Rectangle content)
    {
        string fileName = string.IsNullOrWhiteSpace(_filePath) ? "Preview" : Path.GetFileName(_filePath);
        string message = _looksLikeImage ? "Preview unavailable" : "Press Open to play";
        using var titleFont = new SD.Font("Segoe UI", 13f, SD.FontStyle.Regular);
        using var messageFont = new SD.Font("Segoe UI", 12f, SD.FontStyle.Regular);
        var titleRect = new SD.Rectangle(content.X + 20, content.Y + 22, content.Width - 40, 28);
        var messageRect = new SD.Rectangle(content.X + 20, content.Y + 56, content.Width - 40, 24);
        var flags = WF.TextFormatFlags.HorizontalCenter |
                    WF.TextFormatFlags.VerticalCenter |
                    WF.TextFormatFlags.SingleLine |
                    WF.TextFormatFlags.EndEllipsis;
        WF.TextRenderer.DrawText(g, fileName, titleFont, titleRect, TextColor, flags);
        WF.TextRenderer.DrawText(g, message, messageFont, messageRect, MutedText, flags);
    }

    private void CenterOnScreen()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        using var path = GdiPaths.RoundedRect(new SD.Rectangle(0, 0, Width, Height), 10);
        Region = new SD.Region(path);
    }

    private void CloseOnce()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }

    private static SD.Image? TryLoadImage(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var loaded = SD.Image.FromStream(stream);
            return new SD.Bitmap(loaded);
        }
        catch (Exception ex)
        {
            Log.Error($"Fast quick preview failed for {path}", ex);
            return null;
        }
    }

}
