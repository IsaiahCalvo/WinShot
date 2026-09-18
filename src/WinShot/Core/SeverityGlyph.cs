using System.Drawing.Drawing2D;
using SD = System.Drawing;

namespace WinShot.Core;

/// <summary>
/// The toast's severity mark, drawn rather than typed.
///
/// It used to be three Segoe MDL2 code points in a Label. Two problems with that, and the first
/// is the one a person sees: a typeface's glyphs are not one optical size, so the tick came out
/// 13 device pixels tall and the error mark 22 in the same 24-pixel slot on the same card — the
/// two toasts a user sees most often, one with a mark half the size of the other's. The second is
/// that it put a second icon family inside a window: every other icon in WinShot is drawn on the
/// 24-unit grid at the house stroke weight, and a Microsoft icon font beside them is the loudest
/// tell there is that a UI was not drawn by one hand.
///
/// All three marks are the same ring with a different sign inside it, so all three measure the
/// same, and the weight is the family's: 1.667 units on a 24-unit grid, the number
/// SurveyToolIcons.xaml sets on every glyph in the WPF half.
/// </summary>
internal static class SeverityGlyph
{
    /// <summary>The grid the marks are drawn on, and the stroke on it — the same pair
    /// SurveyToolIcons.xaml uses, so both halves of the app draw one line weight.</summary>
    private const float Grid = 24f;
    private const float StrokeUnits = 1.667f;

    /// <summary>
    /// Draws the mark for <paramref name="severity"/> centred in <paramref name="box"/>, at
    /// whatever size the box allows. Square: the marks are a ring, so the shorter side wins.
    /// </summary>
    public static void Draw(SD.Graphics g, SD.Rectangle box, ToastSeverity severity, SD.Color color)
    {
        float size = Math.Min(box.Width, box.Height);
        if (size < 4) return;

        float unit = size / Grid;
        SmoothingMode previous = g.SmoothingMode;
        PixelOffsetMode previousOffset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // GDI+ samples at pixel CORNERS by default, which lands an anti-aliased ring one whole
        // device pixel right of and below where its geometry says it is: four pixels of air on
        // the left of the toast's mark and two on the right, at every scale.
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        GraphicsState state = g.Save();
        try
        {
            g.TranslateTransform(
                box.Left + ((box.Width - size) / 2f),
                box.Top + ((box.Height - size) / 2f));
            g.ScaleTransform(unit, unit);

            using var pen = new SD.Pen(color, StrokeUnits)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            using var fill = new SD.SolidBrush(color);

            // The ring is the same on all three: one optical size, whatever the sign inside.
            g.DrawEllipse(pen, 3f, 3f, 18f, 18f);

            switch (severity)
            {
                case ToastSeverity.Success:
                    g.DrawLines(pen, new[]
                    {
                        new SD.PointF(7.6f, 12.2f),
                        new SD.PointF(10.7f, 15.3f),
                        new SD.PointF(16.4f, 9.2f),
                    });
                    break;

                case ToastSeverity.Error:
                    // An exclamation, not a cross: the cross reads as "close this" on a card the
                    // user can in fact close by clicking it.
                    g.DrawLine(pen, 12f, 6.9f, 12f, 13.3f);
                    g.FillEllipse(fill, 12f - 0.95f, 16.6f - 0.95f, 1.9f, 1.9f);
                    break;

                default:
                    g.FillEllipse(fill, 12f - 0.95f, 7.4f - 0.95f, 1.9f, 1.9f);
                    g.DrawLine(pen, 12f, 10.8f, 12f, 17.1f);
                    break;
            }
        }
        finally
        {
            g.Restore(state);
            g.PixelOffsetMode = previousOffset;
            g.SmoothingMode = previous;
        }
    }
}
