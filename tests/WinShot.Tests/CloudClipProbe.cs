using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfPath = System.Windows.Shapes.Path;
using WinShot.Editor;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// A revision cloud's crowns bulge OUTSIDE the rectangle they are built from — that is what a
/// cloud IS — but the Path carrying them gets an explicit Width/Height (ResizeBoxShape rebuilds
/// the geometry from those on every resize, and AnnotationTransform.LocalBounds reads them as
/// the authored box, so they are load-bearing), and WPF layout-clips a Stretch.None Shape to
/// that slot. Same mechanism that clipped this editor's props host to nothing.
///
/// Measured before the fix, on a 320x180 bump-2 cloud: geometry spanned
/// (-12.85,-12.85,345.7,205.7) while painted ink was exactly (0,0,320,180) — crown rise 0.0 DIP
/// at stroke 1, 4 AND 12. The crowns rendered as flat-topped stubs; a cloud drew as a rectangle.
///
/// Two cures were compared on the raster rather than argued about:
///   Stretch.Fill    — the reflex, and WRONG: crown rise stayed 0.0, and it scales the geometry,
///                     which Survey forbids outright (cloudAnnotationGeometry.js:10-18, "never a
///                     scale() transform" — it squashes the scallops, non-uniformly at that).
///   no Width/Height — restored the crowns (rise 15.0) but handed the authored box back to the
///                     ink box, so every resize would compound the bulge.
/// What shipped is neither: CloudPath.ForRectangle/ForEllipse now inset the ring the crowns are
/// built from by half a scallop plus half the pen, so the ink lands ON the slot with the crowns
/// intact and the box stays authoritative. This test is what stops that regressing.
/// </summary>
public class CloudClipProbe
{
    /// <summary>
    /// Both SIZED cloud shapes, because they share the whole trap: a rectangle and an ellipse
    /// cloud are the same Path with the same explicit Width/Height and the same layout clip,
    /// differing only in which walk CloudPath.ForBox picks. Ellipse only became reachable when
    /// the five places that rebuilt a cloud's geometry stopped each spelling out "rectangle".
    /// </summary>
    [Theory]
    [InlineData(AnnotationData.TypeRectangle, 1.0)]
    [InlineData(AnnotationData.TypeRectangle, 4.0)]
    [InlineData(AnnotationData.TypeRectangle, 12.0)]
    [InlineData(AnnotationData.TypeEllipse, 1.0)]
    [InlineData(AnnotationData.TypeEllipse, 4.0)]
    [InlineData(AnnotationData.TypeEllipse, 12.0)]
    public void CloudCrownsAreNotClippedToTheirLayoutBox(string type, double strokeWidth)
    {
        const double W = 320, H = 180;
        Rect geom = default, ink = default;
        double crownDepth = 0;
        string note = "";

        var thread = new Thread(() =>
        {
            var shape = new WpfPath
            {
                Data = CloudPath.ForBox(type, new Rect(0, 0, W, H), 2, strokeWidth),
                Stroke = Brushes.Black,
                StrokeThickness = strokeWidth,
                Fill = Brushes.Transparent,
                Width = W,
                Height = H,
            };
            geom = shape.Data.Bounds;

            // Give it plenty of room so nothing but the shape's own layout can clip it.
            const int pad = 60;
            var host = new Canvas { Width = W + 2 * pad, Height = H + 2 * pad, Background = Brushes.White };
            Canvas.SetLeft(shape, pad);
            Canvas.SetTop(shape, pad);
            host.Children.Add(shape);
            host.Measure(new Size(host.Width, host.Height));
            host.Arrange(new Rect(0, 0, host.Width, host.Height));
            host.UpdateLayout();

            int pw = (int)host.Width, ph = (int)host.Height;
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var px = new byte[pw * ph * 4];
            rtb.CopyPixels(px, pw * 4, 0);

            int minX = pw, minY = ph, maxX = -1, maxY = -1;
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    int i = (y * pw + x) * 4;
                    // Anything appreciably darker than the white ground is ink.
                    if (px[i] < 200 && px[i + 1] < 200 && px[i + 2] < 200)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            ink = maxX < 0 ? Rect.Empty
                : new Rect(minX - pad, minY - pad, maxX - minX + 1, maxY - minY + 1);

            // Scallop depth along the TOP edge: for each column across the middle two thirds
            // of the box, find the first inked row; a cloud's crowns make that vary, a clipped
            // cloud is a flat line and makes it constant.
            int lo = int.MaxValue, hi = int.MinValue;
            for (int x = pad + (int)W / 6; x < pad + (int)W * 5 / 6; x++)
            {
                for (int y = 0; y < ph; y++)
                {
                    int i = (y * pw + x) * 4;
                    if (px[i] < 200 && px[i + 1] < 200 && px[i + 2] < 200)
                    {
                        if (y < lo) lo = y;
                        if (y > hi) hi = y;
                        break;
                    }
                }
            }
            crownDepth = hi > lo ? hi - lo : 0;

            string dir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(System.IO.Path.Combine(dir, $"cloud-clip-{type}-w{strokeWidth}.png"));
                enc.Save(fs);
            }
            note = $"{type} stroke={strokeWidth}  geometry={geom}  paintedInk={ink}  crownDepth={crownDepth:F1}";
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // CloudInBox insets the crowns so their INK fills the authored box. So the painted
        // ink must reach all four edges of 0,0,W,H — if the crowns were being layout-clipped
        // the edges would be flat and the rise below would be zero.
        const double slack = 2.0;   // antialiasing
        Assert.True(Math.Abs(ink.Left) <= slack && Math.Abs(ink.Top) <= slack
                    && Math.Abs(ink.Right - W) <= slack && Math.Abs(ink.Bottom - H) <= slack,
            $"the cloud's ink does not fill its authored box: {note}");

        // And the crowns must actually BE crowns: a clipped cloud also fills its box, as a
        // rectangle with flat edges, so filling the box is not on its own proof of anything.
        // A clipped edge measures exactly 0.0 here, so any real variation separates the two.
        // The bar is low on purpose rather than tuned: the raster measure shrinks as the pen
        // fattens (at stroke 12 a 28-unit scallop is nearly filled by its own stroke, and the
        // first-ink row varies by only 3 px) and that is the pen, not a clip.
        Assert.True(crownDepth > 2,
            $"the top edge is flat — the crowns are clipped, not drawn. depth={crownDepth:F1} {note}");
    }
}
