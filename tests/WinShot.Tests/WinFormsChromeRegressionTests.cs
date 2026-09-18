using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using WinShot.Core;
using WinShot.Recording;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// One guard per defect class the round-7 WinForms audit found. Each of these describes a thing
/// a person could see on screen, and each of them was true of a shipping build.
/// </summary>
public class WinFormsChromeRegressionTests
{
    // ---------------------------------------------------------------- type that ignores its box

    /// <summary>
    /// A surface that multiplies every box by its monitor's scale must size its TYPE the same
    /// way. A <c>GraphicsUnit.Point</c> font resolves its pixel size once, against the primary
    /// monitor's device context, so on a 150 % primary with a 100 % second display the first-run
    /// bubble drew 1x boxes around 1.5x type and sliced every row title through the middle; on a
    /// 200 % display the type came out two thirds of the size the layout was drawn for. The audit
    /// measured 43 cells across five surfaces whose box doubled between 96 and 192 DPI while the
    /// ink in it did not move at all.
    ///
    /// Text analysis, so it covers surfaces no test renders.
    /// </summary>
    [Fact]
    public void AHandScaledWinFormsSurfaceNeverSizesItsTypeInPoints()
    {
        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            string text = File.ReadAllText(file);
            // "Hand-scaled" is exactly: it asks RecordingMonitorDpi for its monitor's scale and
            // multiplies its own layout by it.
            if (!text.Contains("RecordingMonitorDpi.ScaleFor", StringComparison.Ordinal)) continue;
            if (PinnedByHand.Contains(Path.GetFileName(file))) continue;

            foreach (Match m in Regex.Matches(text, @"ThemePalette\.(UiFont|IconFont)\("))
                offenders.Add($"{Path.GetFileName(file)}:{m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "These surfaces scale their boxes by hand and size their type in points, which is the "
            + "PRIMARY monitor's size and not theirs — the boxes move and the type does not. Use "
            + "ThemePalette.ScaledUiFont / ScaledIconFont with the same scale the layout uses: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The same thing measured rather than read: build one toast at 100 % and one at 200 % and the
    /// type has to grow with the card. Uses the same ScaleSource seam the audit harness does.
    /// </summary>
    [Fact]
    public void AToastAtTwoHundredPercentDrawsTypeTwiceTheSize()
    {
        int small = TitleFontHeight(1.0);
        int large = TitleFontHeight(2.0);
        Assert.True(large >= small * 1.8,
            $"a toast's title face is {small} px at 100 % and {large} px at 200 %; the card doubles, "
            + "so the type has to as well");

        static int TitleFontHeight(double scale)
        {
            RecordingMonitorDpi.ScaleSource = _ => scale;
            try
            {
                using var toast = new ToastWindow(
                    "Saved", "shot.png", anchorScreen: null, onOpen: null, ToastSeverity.Success);
                // The title is the second control added: severity mark, title, body.
                return toast.Controls[toast.Controls.Count - 2].Font.Height;
            }
            finally
            {
                RecordingMonitorDpi.ScaleSource = null;
            }
        }
    }

    // ---------------------------------------------------------------- white on a saturated fill

    /// <summary>
    /// <c>DarkButton</c> puts a WHITE label on any fill that is saturated, so every saturated fill
    /// the app hands one has to carry white at the 4.5:1 text floor. White on the raw accent is
    /// 3.65:1 — which is why <see cref="ThemePalette.AccentDeep"/> exists — and three buttons were
    /// still using the raw accent: Start capture and Done on the scrolling bar, and Start in the
    /// recording options card.
    /// </summary>
    [Fact]
    public void EverySaturatedWinFormsFillCarriesWhiteAboveTheTextFloor()
    {
        (string Name, SD.Color Colour)[] fills =
        {
            ("AccentDeep", ThemePalette.AccentDeep),
            ("AccentPressed", ThemePalette.AccentPressed),
            ("RedDeep", ThemePalette.RedDeep),
        };
        foreach ((string name, SD.Color fill) in fills)
        {
            Assert.True(fill.GetSaturation() >= 0.25f, $"{name} is not a saturated fill any more");
            Assert.True(Contrast(SD.Color.White, fill) >= 4.5,
                $"white on {name} is {Contrast(SD.Color.White, fill):F2}:1");
        }

        // And the trap: the raw accent is NOT one of them, so nothing may hand it to a DarkButton.
        Assert.True(Contrast(SD.Color.White, ThemePalette.Accent) < 4.5,
            "if the raw accent now carries white, delete AccentDeep rather than leaving two");

        var offenders = new List<string>();
        foreach (string file in SourceFiles())
        {
            if (PinnedByHand.Contains(Path.GetFileName(file))) continue;
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(file),
                         @"ThemePalette\.Accent\s*,\s*SD\.Color\.White"))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }
        Assert.True(offenders.Count == 0,
            "White on the raw accent is 3.65:1. Use ThemePalette.AccentDeep: "
            + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------- one context-menu plate

    /// <summary>
    /// Right-click in the editor and right-click the tray icon and you used to get two different
    /// cards: the WPF menus paint Theme.xaml's Elevated (#323236, declared for "popups / flyouts /
    /// context menus"), and the WinForms colour table held a fourth dark grey of its own that
    /// nothing declared.
    /// </summary>
    [Fact]
    public void EveryDarkMenuPaintsOnThePalettesPopupColour()
    {
        var renderer = new DarkDropDown.DarkMenuRenderer();
        Assert.Equal(ThemePalette.Elevated, renderer.ColorTable.ToolStripDropDownBackground);
        Assert.Equal(ThemePalette.Elevated, renderer.ColorTable.ImageMarginGradientBegin);
        // And the same value the WPF half reads, so the two halves cannot drift apart silently.
        string theme = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "WinShot", "Theme", "Theme.xaml"));
        Match declared = Regex.Match(theme, @"ElevatedColor"">#FF([0-9A-Fa-f]{6})<");
        Assert.True(declared.Success, "Theme.xaml no longer declares ElevatedColor");
        Assert.Equal(
            declared.Groups[1].Value.ToUpperInvariant(),
            $"{ThemePalette.Elevated.R:X2}{ThemePalette.Elevated.G:X2}{ThemePalette.Elevated.B:X2}");
    }

    /// <summary>
    /// The tray menu's version header is a disabled row, and the base renderer throws the colour
    /// we hand it away and paints disabled rows through ControlPaint's own light-theme grey: the
    /// header came out #6D6D6D on the card, 3.03:1, the least readable thing in the app.
    /// </summary>
    [Fact]
    public void ADisabledMenuRowIsStillReadable()
    {
        double contrast = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var menu = new WF.ContextMenuStrip
                {
                    Renderer = new DarkDropDown.DarkMenuRenderer(),
                    ShowImageMargin = false,
                };
                var header = new WF.ToolStripMenuItem("WinShot 1.0.0") { Enabled = false };
                menu.Items.Add(header);
                menu.Items.Add(new WF.ToolStripMenuItem("Capture region"));
                menu.Show(new SD.Point(0, 0));
                using var image = new SD.Bitmap(
                    menu.Width, menu.Height, SD.Imaging.PixelFormat.Format24bppRgb);
                menu.DrawToBitmap(image, new SD.Rectangle(SD.Point.Empty, image.Size));
                menu.Close();

                // The brightest pixel in the header's row is the face the label was painted in.
                SD.Rectangle row = header.Bounds;
                SD.Color plate = ThemePalette.Elevated;
                SD.Color ink = plate;
                double best = -1;
                for (int y = Math.Max(0, row.Top); y < Math.Min(image.Height, row.Bottom); y++)
                    for (int x = Math.Max(0, row.Left); x < Math.Min(image.Width, row.Right); x++)
                    {
                        SD.Color c = image.GetPixel(x, y);
                        double l = (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B);
                        if (l <= best) continue;
                        best = l;
                        ink = c;
                    }
                contrast = Contrast(ink, plate);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.True(contrast >= 4.5,
            $"the menu's version header reads {contrast:F2}:1 against its own card");
    }

    // ---------------------------------------------------------------- the countdown's own ring

    /// <summary>
    /// The countdown window IS the ring — its Region is an ellipse — so the cancel hint's usable
    /// width is the circle's chord at its own height, not the window's. It used to take the full
    /// width and both ends of the sentence were painted on top of the accent arc.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheCountdownHintStaysInsideTheRing(double scale)
    {
        var client = new SD.Size(Px(170), Px(170));
        SD.Rectangle hint = FastRecordingCountdownWindow.HintBounds(client, scale);
        double radius = (client.Width / 2.0) - Math.Max(3, Px(3));
        double cx = client.Width / 2.0, cy = client.Height / 2.0;

        foreach (SD.Point corner in new[]
                 {
                     new SD.Point(hint.Left, hint.Top), new SD.Point(hint.Right, hint.Top),
                     new SD.Point(hint.Left, hint.Bottom), new SD.Point(hint.Right, hint.Bottom),
                 })
        {
            double dx = corner.X - cx, dy = corner.Y - cy;
            Assert.True(Math.Sqrt((dx * dx) + (dy * dy)) <= radius,
                $"at {scale:F2} the hint's corner {corner} is outside a ring of {radius:F1}");
        }

        // And the sentence has to fit in what is left, or it ellipses and loses its last word.
        using SD.Font face = ThemePalette.ScaledUiFont(ThemePalette.TextBodyPt, scale);
        int width = WF.TextRenderer.MeasureText(
            "Click or Esc to cancel", face,
            new SD.Size(int.MaxValue, int.MaxValue),
            WF.TextFormatFlags.SingleLine | WF.TextFormatFlags.NoPadding).Width;
        Assert.True(width <= hint.Width,
            $"at {scale:F2} the hint needs {width} px and the ring leaves {hint.Width}");

        int Px(int logical) => RecordingMonitorDpi.Px(logical, scale);
    }

    // ---------------------------------------------------------------- one severity mark size

    /// <summary>
    /// The toast's three severity marks used to be three Segoe MDL2 code points, and a typeface's
    /// glyphs are not one optical size: the tick measured 13 device pixels tall and the error mark
    /// 22 in the same slot on the same card. Drawn, they are the same ring with a different sign
    /// inside it, so all three measure the same.
    /// </summary>
    [Fact]
    public void TheThreeSeverityMarksAreOneSize()
    {
        var boxes = new Dictionary<ToastSeverity, SD.Rectangle>();
        foreach (ToastSeverity severity in Enum.GetValues<ToastSeverity>())
            boxes[severity] = MarkInk(severity);

        SD.Rectangle first = boxes[ToastSeverity.Info];
        Assert.True(first.Width > 20, $"the mark barely drew anything: {first}");
        foreach ((ToastSeverity severity, SD.Rectangle box) in boxes)
        {
            Assert.True(Math.Abs(box.Width - first.Width) <= 1 && Math.Abs(box.Height - first.Height) <= 1,
                $"{severity} measures {box.Width}x{box.Height} where Info measures "
                + $"{first.Width}x{first.Height}");
            // Centred in its own box, both ways, within a pixel.
            Assert.True(Math.Abs(box.Left - (48 - box.Right)) <= 1,
                $"{severity} sits {box.Left} from the left and {48 - box.Right} from the right");
            Assert.True(Math.Abs(box.Top - (48 - box.Bottom)) <= 1,
                $"{severity} sits {box.Top} from the top and {48 - box.Bottom} from the bottom");
        }

        static SD.Rectangle MarkInk(ToastSeverity severity)
        {
            using var bitmap = new SD.Bitmap(48, 48, SD.Imaging.PixelFormat.Format24bppRgb);
            using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
            {
                g.Clear(ThemePalette.ToolbarBg);
                SeverityGlyph.Draw(
                    g, new SD.Rectangle(0, 0, 48, 48), severity, ThemePalette.TextPrimary);
            }
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < 48; y++)
                for (int x = 0; x < 48; x++)
                {
                    SD.Color c = bitmap.GetPixel(x, y);
                    if (c.R - ThemePalette.ToolbarBg.R < 40) continue;
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
            return maxX < 0 ? SD.Rectangle.Empty : SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
    }

    // ---------------------------------------------------------------- rows that overlap

    /// <summary>
    /// The first-run card's title and subtitle boxes overlapped by two design pixels, and so did
    /// every row's title and hint — so a descender in one row was painted inside the box of the
    /// next and the lower row had no clearance above it at all. On the app's first screen.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TheFirstRunCardsTextRowsDoNotOverlap(double scale)
    {
        var body = new SD.Rectangle(0, 0, RecordingMonitorDpi.Px(320, scale), RecordingMonitorDpi.Px(240, scale));
        (SD.Rectangle title, SD.Rectangle subtitle) = WinShot.Core.FirstRunWelcomeWindow.HeaderBounds(body, scale);
        Assert.False(title.IntersectsWith(subtitle), $"header {title} overlaps {subtitle}");

        var row = new SD.Size(body.Width - RecordingMonitorDpi.Px(14, scale), RecordingMonitorDpi.Px(46, scale));
        (SD.Rectangle glyph, SD.Rectangle rowTitle, SD.Rectangle hint) =
            WinShot.Core.FirstRunWelcomeWindow.RowBounds(row, scale);
        Assert.False(rowTitle.IntersectsWith(hint), $"row title {rowTitle} overlaps hint {hint}");
        Assert.False(glyph.IntersectsWith(rowTitle), $"row glyph {glyph} overlaps title {rowTitle}");
        Assert.True(hint.Bottom <= row.Height, $"the hint {hint} runs past a {row.Height} px row");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The quick-actions card and everything it owns. It is pinned by hand — idle look, geometry,
    /// hover treatment — and its sizes were measured against a shipping build rather than derived,
    /// so it is deliberately outside every rule here.
    /// </summary>
    private static readonly HashSet<string> PinnedByHand = new(StringComparer.OrdinalIgnoreCase)
    {
        "FastQuickActionsWindow.cs",
        "QuickActionMenuItem.cs",
        "QuickAccessTooltipWindow.cs",
    };

    private static double Contrast(SD.Color a, SD.Color b)
    {
        double la = Relative(a), lb = Relative(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

        static double Relative(SD.Color c)
        {
            static double Ch(int v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return (0.2126 * Ch(c.R)) + (0.7152 * Ch(c.G)) + (0.0722 * Ch(c.B));
        }
    }

    private static IEnumerable<string> SourceFiles()
    {
        string src = Path.Combine(RepoRoot(), "src", "WinShot");
        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return f;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "WinShot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
