using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WinShot.Core;

/// <summary>
/// A cheap check that a warm freeze frame shows the screen as it is now.
///
/// The warm duplication answers in ~10 ms because it hands back the last frame it copied when
/// nothing new has arrived. If Windows stops delivering frames for a screen without saying so,
/// that copy goes stale and the selector freezes over a picture of the past: on Isaiah's
/// laptop panel (2026-11-03) the frozen shot showed an old desktop with no Start menu, while
/// the two external screens were right. This reads three live rows of each screen with GDI
/// (~1 ms) and compares them with the same rows of the frame.
/// </summary>
internal static class FreezeFrameCheck
{
    /// <summary>A channel difference above this is a different picture, not noise.</summary>
    internal const int PixelTolerance = 40;

    /// <summary>Share of sampled pixels that may differ before the frame counts as stale.
    /// High enough that a playing video or a ticking clock does not trip it; a stale frame
    /// of a desktop that has since changed differs across far more of each row.</summary>
    internal const double StaleShare = 0.15;

    /// <summary>The rows sampled on each screen, as fractions of its height.</summary>
    internal static readonly double[] RowFractions = { 0.2, 0.5, 0.8 };

    private const int Step = 4;

    /// <summary>
    /// The first screen whose rows in <paramref name="frame"/> differ from the live rows
    /// <paramref name="readRow"/> returns, or null when every screen matches. The frame covers
    /// <paramref name="frameBounds"/>; screens and rows are in the same physical coordinates.
    /// A screen whose rows cannot be read is skipped rather than called stale.
    /// </summary>
    public static (Rectangle Screen, double Share)? FindStaleScreen(
        Bitmap frame,
        Rectangle frameBounds,
        IEnumerable<Rectangle> screens,
        Func<Rectangle, Bitmap?> readRow)
    {
        foreach (Rectangle screen in screens)
        {
            Rectangle visible = Rectangle.Intersect(screen, frameBounds);
            if (visible.Width < Step || visible.Height < 1)
                continue;

            int sampled = 0;
            int differing = 0;
            foreach (double fraction in RowFractions)
            {
                int y = visible.Top + (int)(visible.Height * fraction);
                var row = new Rectangle(visible.Left, Math.Min(y, visible.Bottom - 1), visible.Width, 1);
                using Bitmap? live = readRow(row);
                if (live is null)
                    continue;
                (int s, int d) = CompareRow(frame, row.X - frameBounds.X, row.Y - frameBounds.Y, live);
                sampled += s;
                differing += d;
            }

            if (sampled == 0)
                continue;
            double share = differing / (double)sampled;
            if (share > StaleShare)
                return (screen, share);
        }

        return null;
    }

    private static (int Sampled, int Differing) CompareRow(Bitmap frame, int x, int y, Bitmap live)
    {
        int width = Math.Min(live.Width, frame.Width - x);
        if (width < 1 || y < 0 || y >= frame.Height || x < 0)
            return (0, 0);

        BitmapData a = frame.LockBits(new Rectangle(x, y, width, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            BitmapData b = live.LockBits(new Rectangle(0, 0, width, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var pa = new int[width];
                var pb = new int[width];
                Marshal.Copy(a.Scan0, pa, 0, width);
                Marshal.Copy(b.Scan0, pb, 0, width);
                int sampled = 0;
                int differing = 0;
                for (int i = 0; i < width; i += Step)
                {
                    int ca = pa[i], cb = pb[i];
                    int diff = Math.Max(
                        Math.Abs((ca & 0xFF) - (cb & 0xFF)),
                        Math.Max(
                            Math.Abs(((ca >> 8) & 0xFF) - ((cb >> 8) & 0xFF)),
                            Math.Abs(((ca >> 16) & 0xFF) - ((cb >> 16) & 0xFF))));
                    sampled++;
                    if (diff > PixelTolerance)
                        differing++;
                }
                return (sampled, differing);
            }
            finally
            {
                live.UnlockBits(b);
            }
        }
        finally
        {
            frame.UnlockBits(a);
        }
    }
}
