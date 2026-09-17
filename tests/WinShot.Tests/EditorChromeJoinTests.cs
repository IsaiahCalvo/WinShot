using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The editor's chrome has to read as ONE frame.
///
/// Isaiah, 2026-09-17: "The right rail dies into the top toolbar because the top toolbar has
/// no bottom border." The rail carries a left rule down rows 1-2 and the bottom bar carries a
/// top rule, but row 0 carried none, so the rail's rule ran up and stopped against nothing.
/// Asserted by scanning the rendered pixels, not by reading BorderThickness back — a border
/// that is set but painted over, rounded away at a fractional DPI, or covered by a sibling
/// still fails the user, and only the raster can tell.
/// </summary>
public class EditorChromeJoinTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]   // Isaiah's primary display runs at 150%.
    [InlineData(192)]
    public void ToolbarRailAndBottomBarShareOneHairline(double dpi)
    {
        var r = Render(dpi);
        // Stood down because another test owns the Application — see Render.
        if (r is null) return;
        double scale = dpi / 96.0;

        // 1. The toolbar closes with a horizontal rule across the FULL width.
        Assert.True(r.ToolbarBottomRun >= r.Width * 0.98,
            $"the toolbar's bottom hairline covers {r.ToolbarBottomRun}/{r.Width} px at {dpi} DPI; " +
            "it must run the whole width or the rail has nothing to meet.");

        // 2. The rail closes with a vertical rule down its whole height.
        Assert.True(r.RailLeftRun >= r.RailHeight * 0.98,
            $"the rail's left hairline covers {r.RailLeftRun}/{r.RailHeight} px at {dpi} DPI.");

        // 3. Each rule is the theme's Border token, not a hand-picked grey.
        // Border is #1FFFFFFF — a 12%-white WASH, not an opaque colour — so the three rules
        // legitimately paint to different values over their own surfaces, and comparing the
        // painted pixels to each other (or to the raw token) would be a category error.
        // What must hold is that each one is that token composited over its own background,
        // which is the same rule tools/scan-editor-palette.py applies to the whole strip.
        // A missing border is ~26/255 away per channel; the tolerance is 3.
        AssertIsBorderWashOver(r.ToolbarRuleColor, r.ToolbarSurface, "toolbar bottom", dpi);
        AssertIsBorderWashOver(r.RailRuleColor, r.RailSurface, "rail left", dpi);
        AssertIsBorderWashOver(r.BottomBarRuleColor, r.BottomBarSurface, "bottom bar top", dpi);

        // 5. The corner itself: the rail's rule and the toolbar's rule must actually MEET.
        // A gap here is exactly the "dies into the toolbar" look, and it is what a
        // rounded-away fractional-DPI edge produces.
        Assert.True(r.CornerGap <= Math.Ceiling(scale),
            $"there are {r.CornerGap} px of unruled chrome between the toolbar's bottom rule " +
            $"and the top of the rail's left rule at {dpi} DPI.");
    }

    /// <summary>
    /// A rule painted with the Border token reads as that token composited over whatever
    /// surface it sits on. Recompute the composite and compare.
    /// </summary>
    private static void AssertIsBorderWashOver(int rule, int surface, string what, double dpi)
    {
        var b = (Color)Application.Current!.Resources["BorderColor"];
        double a = b.A / 255.0;
        int Blend(int dst, int src) => (int)Math.Round(dst + (src - dst) * a);

        int sr = (surface >> 16) & 0xFF, sg = (surface >> 8) & 0xFF, sb = surface & 0xFF;
        int er = Blend(sr, b.R), eg = Blend(sg, b.G), eb = Blend(sb, b.B);
        int rr = (rule >> 16) & 0xFF, rg = (rule >> 8) & 0xFF, rb = rule & 0xFF;

        int worst = Math.Max(Math.Abs(rr - er), Math.Max(Math.Abs(rg - eg), Math.Abs(rb - eb)));
        Assert.True(worst <= 3,
            $"the {what} rule at {dpi} DPI paints #{rr:X2}{rg:X2}{rb:X2} over surface " +
            $"#{sr:X2}{sg:X2}{sb:X2}; the Border token would give #{er:X2}{eg:X2}{eb:X2} " +
            $"(off by {worst}). Either the rule is missing or it is not the Border token.");
    }

    private sealed record Scan(
        int Width, int RailHeight, int ToolbarBottomRun, int RailLeftRun, int CornerGap,
        int ToolbarRuleColor, int RailRuleColor, int BottomBarRuleColor,
        int ToolbarSurface, int RailSurface, int BottomBarSurface);

    private static Scan? Render(double dpi)
    {
        Exception? failure = null;
        Scan? scan = null;
        var thread = new Thread(() =>
        {
            try
            {
                // This assembly gets exactly ONE System.Windows.Application, and the thread
                // that creates it owns it for good — after that thread exits, Application.Current
                // still points at a Dispatcher nobody is running, so a later test can neither
                // reuse it nor replace it. ThemedWindowTests is the established owner (it builds
                // one and shuts it down), so claiming it here would fail that test with "Cannot
                // create more than one System.Windows.Application instance in the same AppDomain"
                // — which is exactly what these tests did to it. Same reason every render harness
                // in this suite is env-gated: one editor-window class per host, run deliberately.
                // The handoff lists the command.
                if (Environment.GetEnvironmentVariable("WINSHOT_EDITOR_TESTS") != "1") return;
                if (Application.Current is { } inherited && !inherited.CheckAccess())
                    return;
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();
                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var bmp = new SD.Bitmap(1200, 800);
                using (var g = SD.Graphics.FromImage(bmp)) g.Clear(SD.Color.White);

                var editor = new EditorWindow(bmp, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000, Top = -6000, Width = 1400, Height = 900, ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 25; i++) { Pump(); Thread.Sleep(15); }
                editor.UpdateLayout();
                Pump();

                var root = (FrameworkElement)editor.Content;
                var bar = (FrameworkElement)editor.FindName("TopToolbar");
                var rail = (FrameworkElement)editor.FindName("ZoomRail");
                var bottom = (FrameworkElement)editor.FindName("EditorBottomBar");

                double s = dpi / 96.0;
                int pw = (int)Math.Ceiling(root.ActualWidth * s);
                int ph = (int)Math.Ceiling(root.ActualHeight * s);
                var rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(root);
                var px = new byte[pw * ph * 4];
                rtb.CopyPixels(px, pw * 4, 0);
                int At(int x, int y)
                {
                    int i = (y * pw + x) * 4;
                    return (px[i + 2] << 16) | (px[i + 1] << 8) | px[i];  // Pbgra -> rgb
                }

                Point P(FrameworkElement fe) => fe.TransformToAncestor(root).Transform(new Point(0, 0));

                // The toolbar's bottom rule is the last device row of its box.
                int barBottom = (int)Math.Round((P(bar).Y + bar.ActualHeight) * s) - 1;
                int railLeft = (int)Math.Round(P(rail).X * s);
                int railTop = (int)Math.Round(P(rail).Y * s);
                int railBot = (int)Math.Round((P(rail).Y + rail.ActualHeight) * s) - 1;
                int botTop = (int)Math.Round(P(bottom).Y * s);

                int step = (int)Math.Ceiling(s);      // one logical pixel in device pixels
                int ruleBar = At(pw / 4, barBottom);
                int ruleRail = At(railLeft, (railTop + railBot) / 2);
                int ruleBottom = At(pw / 4, botTop);
                // The surface each rule sits on, sampled just inside its own box so the
                // neighbouring panel's fill can never be picked up by mistake.
                int surfBar = At(pw / 4, barBottom - 2 * step);
                int surfRail = At(railLeft + 2 * step, (railTop + railBot) / 2);
                int surfBottom = At(pw / 4, botTop + 2 * step);

                int barRun = 0;
                for (int x = 0; x < pw; x++) if (At(x, barBottom) == ruleBar) barRun++;
                int railRun = 0;
                for (int y = railTop; y <= railBot && y < ph; y++)
                    if (At(railLeft, y) == ruleRail) railRun++;

                // Walk down the rail's left column from the toolbar's rule and count how many
                // rows are NOT the rule before it starts.
                int gap = 0;
                for (int y = barBottom + 1; y <= railBot && y < ph; y++)
                {
                    if (At(railLeft, y) == ruleRail) break;
                    gap++;
                }

                scan = new Scan(pw, railBot - railTop + 1, barRun, railRun, gap,
                    ruleBar, ruleRail, ruleBottom, surfBar, surfRail, surfBottom);
                editor.Close();
                Pump();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return scan;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
