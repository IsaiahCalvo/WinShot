using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Capture;

internal static class FastSelectorLoupeRenderer
{
    private const int LoupeSize = 120;
    private const int LoupeZoom = 8;
    private static bool _failureLogged;

    public static void Draw(
        SD.Graphics g,
        SD.Size clientSize,
        SD.Rectangle virtualScreen,
        SD.Point cursorClient,
        SD.Point cursorScreen)
    {
        var loupe = FastSelectorLoupeLayout.Calculate(
            clientSize,
            virtualScreen,
            cursorClient,
            cursorScreen,
            LoupeSize,
            LoupeZoom);
        if (!loupe.IsVisible)
            return;

        try
        {
            using var sample = CaptureService.CaptureScreenRegionWithoutLayeredWindows(loupe.SourceScreen);
            DrawSample(g, sample, loupe);
            DrawFrame(g, loupe);
            DrawCoordinates(g, clientSize, loupe);
        }
        catch (Exception ex)
        {
            if (_failureLogged)
                return;

            _failureLogged = true;
            Log.Error("Fast selector loupe draw failed", ex);
        }
    }

    private static void DrawSample(SD.Graphics g, SD.Bitmap sample, FastSelectorLoupe loupe)
    {
        var state = g.Save();
        using var path = new SD.Drawing2D.GraphicsPath();
        path.AddEllipse(loupe.Bounds);
        g.SetClip(path);
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(sample, loupe.Bounds);
        g.Restore(state);
    }

    private static void DrawFrame(SD.Graphics g, FastSelectorLoupe loupe)
    {
        using var border = new SD.Pen(SD.Color.FromArgb(220, 255, 255, 255), 2);
        using var shadow = new SD.Pen(SD.Color.FromArgb(150, 0, 0, 0), 4);
        using var guide = new SD.Pen(SD.Color.FromArgb(220, ThemePalette.Accent), 1);
        g.DrawEllipse(shadow, loupe.Bounds);
        g.DrawEllipse(border, loupe.Bounds);
        g.DrawLine(guide, loupe.CrosshairCenter.X, loupe.Bounds.Top + 4, loupe.CrosshairCenter.X, loupe.Bounds.Bottom - 4);
        g.DrawLine(guide, loupe.Bounds.Left + 4, loupe.CrosshairCenter.Y, loupe.Bounds.Right - 4, loupe.CrosshairCenter.Y);
    }

    private static void DrawCoordinates(SD.Graphics g, SD.Size clientSize, FastSelectorLoupe loupe)
    {
        using var font = new SD.Font("Consolas", 8.5f);
        SD.Size size = WF.TextRenderer.MeasureText(loupe.Coordinates, font);
        int left = Math.Clamp(loupe.Bounds.Left + 18, 0, Math.Max(0, clientSize.Width - size.Width - 12));
        int top = Math.Clamp(loupe.Bounds.Bottom + 4, 0, Math.Max(0, clientSize.Height - size.Height - 6));
        var bg = new SD.Rectangle(left, top, size.Width + 12, size.Height + 4);
        using var brush = new SD.SolidBrush(SD.Color.FromArgb(210, 30, 30, 30));
        g.FillRectangle(brush, bg);
        WF.TextRenderer.DrawText(g, loupe.Coordinates, font, new SD.Point(left + 6, top + 2), SD.Color.White);
    }
}
