using System.Drawing.Drawing2D;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

internal readonly record struct QuickAccessTooltipLayout(
    SD.Size Size,
    SD.Rectangle TextBounds,
    int CornerRadius)
{
    public static QuickAccessTooltipLayout Calculate(string text, SD.Font font, int dpi)
    {
        int normalizedDpi = Math.Clamp(dpi, 96, 480);
        double scale = normalizedDpi / 96d;
        var flags = WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding;
        using var probe = new SD.Bitmap(1, 1);
        probe.SetResolution(normalizedDpi, normalizedDpi);
        using var graphics = SD.Graphics.FromImage(probe);
        SD.Size textSize = WF.TextRenderer.MeasureText(graphics, text, font, SD.Size.Empty, flags);
        int horizontalPadding = Scale(10, scale);
        int verticalPadding = Scale(6, scale);
        var size = new SD.Size(
            textSize.Width + horizontalPadding * 2,
            textSize.Height + verticalPadding * 2);
        return new QuickAccessTooltipLayout(
            size,
            new SD.Rectangle(horizontalPadding, verticalPadding, textSize.Width, textSize.Height),
            Scale(8, scale));
    }

    public static SD.Point Place(
        SD.Rectangle workingArea,
        SD.Size tooltipSize,
        SD.Rectangle anchorScreen,
        SD.Rectangle cardScreen,
        int dpi)
    {
        double scale = Math.Clamp(dpi, 96, 480) / 96d;
        int gap = Scale(8, scale);
        int x = anchorScreen.Left + (anchorScreen.Width - tooltipSize.Width) / 2;
        int y = cardScreen.Top - tooltipSize.Height - gap;
        if (y < workingArea.Top)
            y = cardScreen.Bottom + gap;

        return new SD.Point(
            Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - tooltipSize.Width)),
            Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - tooltipSize.Height)));
    }

    private static int Scale(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));
}

internal static class QuickAccessTooltipRenderer
{
    public static void Draw(
        SD.Graphics graphics,
        QuickAccessTooltipLayout layout,
        string text,
        SD.Font font,
        QuickAccessOverlayVisuals visuals)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(visuals.TooltipFill);
        var surface = new SD.Rectangle(0, 0, layout.Size.Width - 1, layout.Size.Height - 1);
        using (var path = GdiPaths.RoundedRect(surface, layout.CornerRadius))
        using (var fill = new SD.SolidBrush(visuals.TooltipFill))
        using (var border = new SD.Pen(visuals.TooltipBorder, 1))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        WF.TextRenderer.DrawText(
            graphics,
            text,
            font,
            layout.TextBounds,
            visuals.TooltipText,
            WF.TextFormatFlags.HorizontalCenter |
            WF.TextFormatFlags.VerticalCenter |
            WF.TextFormatFlags.SingleLine |
            WF.TextFormatFlags.NoPadding);
    }

    public static SD.Bitmap RenderEvidence(
        string text,
        QuickAccessOverlayVisuals visuals,
        int dpi = 96)
    {
        using var font = ThemePalette.UiFont(8.25f);
        QuickAccessTooltipLayout layout = QuickAccessTooltipLayout.Calculate(text, font, dpi);
        var bitmap = new SD.Bitmap(layout.Size.Width, layout.Size.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = SD.Graphics.FromImage(bitmap);
        Draw(graphics, layout, text, font, visuals);
        return bitmap;
    }
}

internal sealed class QuickAccessTooltipWindow : WF.Form
{
    private const int CsDropShadow = 0x00020000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private readonly string _text;
    private readonly QuickAccessOverlayVisuals _visuals;
    private readonly SD.Font _font = ThemePalette.UiFont(8.25f);
    private readonly QuickAccessTooltipLayout _layout;
    private readonly int _dpi;

    public QuickAccessTooltipWindow(string text, QuickAccessOverlayVisuals visuals, int dpi)
    {
        _text = text;
        _visuals = visuals;
        _dpi = Math.Clamp(dpi, 96, 480);
        _layout = QuickAccessTooltipLayout.Calculate(text, _font, _dpi);

        AccessibleName = text;
        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = visuals.TooltipFill;
        ClientSize = _layout.Size;
        DoubleBuffered = true;
        FormBorderStyle = WF.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            WF.CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnPaint(WF.PaintEventArgs e)
        => QuickAccessTooltipRenderer.Draw(e.Graphics, _layout, _text, _font, _visuals);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        using var path = GdiPaths.RoundedRect(new SD.Rectangle(SD.Point.Empty, ClientSize), _layout.CornerRadius);
        Region?.Dispose();
        Region = new SD.Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }

    public void ShowNear(WF.Form owner, SD.Rectangle anchorScreen, SD.Rectangle cardScreen)
    {
        SD.Rectangle workingArea = WF.Screen.FromRectangle(cardScreen).WorkingArea;
        Location = QuickAccessTooltipLayout.Place(workingArea, Size, anchorScreen, cardScreen, _dpi);
        Show(owner);
    }
}
