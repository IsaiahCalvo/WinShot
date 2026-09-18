using System.Globalization;
using System.IO;
using System.Reflection;
using WinShot.Core;
using WinShot.Overlay;
using WinShot.Pin;
using WinShot.Recording;
using WinShot.Scrolling;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The other half of <see cref="VisualAuditHarness"/>. That one renders WPF windows; roughly a
/// third of what WinShot puts on screen is Windows Forms — every toast, the tray menu, the
/// first-run bubble, the keystroke pill, the countdown ring, the recording bar, the scrolling
/// chrome, the pin chrome — and <c>RenderTargetBitmap</c> cannot photograph any of it. Two
/// visual-audit rounds therefore measured none of it.
///
/// It measures the same thing the WPF harness does — INK against the box that holds it — by a
/// different route. The WPF harness renders twice and subtracts, because a WPF window sits over
/// a screenshot and the background is not a constant. WinForms chrome in this app is a flat dark
/// plate, so the ink is simply everything BRIGHTER than the plate:
///
///   * the modal colour inside a cell's box is the plate it sits on;
///   * ink is every pixel more than <see cref="InkStep"/> brighter than that;
///   * a column that is ink over 85 % of its height is a hairline or a cell divider, not a
///     glyph, and the same for a row. Both are struck out before the ink box is taken.
///
/// One render per surface per scale, no state to save and restore, and it works identically for
/// a form that hosts Labels and for one that paints its own text in OnPaint — which the
/// blank-the-text trick does not.
///
/// The four scales come from <see cref="RecordingMonitorDpi.ScaleSource"/>, the seam every
/// hand-laid-out WinForms surface in the app already reads its scale through. That is what makes
/// the NOSCALE flag possible: render the same cell at 1.0 and at 2.0, and if the box doubled
/// while the ink did not, the type in it is not scaling with the box it lives in.
///
/// Output: full-surface PNGs, a 4x nearest-neighbour crop per flagged cell (nearest-neighbour on
/// purpose — these are device pixels and the point is to see them), audit.tsv with a row per
/// cell, and colours.tsv, a census of every colour each surface paints against the palette.
/// Gated on WINSHOT_WINFORMS_AUDIT=1; output dir is WINSHOT_WINFORMS_AUDIT_OUT.
/// </summary>
public class WinFormsAuditHarness
{
    /// <summary>How much brighter than its plate a pixel has to be to count as ink. Below the
    /// faintest thing the app draws on purpose (BorderStrong, 0x33 white, lifts a #262628 plate
    /// by ~27) and well above PNG/AA noise.</summary>
    private const int InkStep = 22;

    private static readonly (int Dpi, double Scale)[] Steps =
    {
        (96, 1.0), (120, 1.25), (144, 1.5), (192, 2.0),
    };

    private const string Header =
        "surface\tdpi\tcell\ttype\ttext\trow\tctrl_x\tctrl_y\tctrl_w\tctrl_h\tink_w\tink_h\t"
        + "gap_l\tgap_t\tgap_r\tgap_b\tink_pct_h\tstroke\tink_rgb\tbg_rgb\tcontrast\tflags";

    [Fact]
    public void AuditEveryWinFormsSurface()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_WINFORMS_AUDIT") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_WINFORMS_AUDIT_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-winforms-audit");
        string cropDir = Path.Combine(outDir, "crops");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(cropDir);

        var rows = new List<string> { Header };
        var colours = new List<string> { "surface\tdpi\targb\tpixels\tshare_pct\ttoken" };
        // cell -> ink height and box height per scale, for the cross-scale NOSCALE pass.
        var scaleTrack = new Dictionary<string, Dictionary<int, (int InkH, int BoxH)>>(StringComparer.Ordinal);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach ((int dpi, double scale) in Steps)
                {
                    // The seam every manually-scaled WinForms surface reads. Set before the
                    // surface is constructed: these forms lay themselves out in their ctor.
                    RecordingMonitorDpi.ScaleSource = _ => scale;
                    foreach ((string name, Func<double, Shot?> take) in Surfaces())
                    {
                        Shot? shot = null;
                        try
                        {
                            shot = take(scale);
                            if (shot is null) continue;
                            shot.Image.Save(
                                Path.Combine(outDir, $"{name}-{dpi}.png"), SD.Imaging.ImageFormat.Png);
                            Measure(shot, name, dpi, scale, cropDir, rows, scaleTrack);
                            Census(shot.Image, name, dpi, colours);
                        }
                        catch (Exception ex)
                        {
                            rows.Add($"{name}\t{dpi}\t-\t-\tFAILED: {ex.GetType().Name}: "
                                + $"{Flat(ex.Message)}\t-\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\tERROR");
                        }
                        finally
                        {
                            shot?.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                RecordingMonitorDpi.ScaleSource = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        File.WriteAllLines(Path.Combine(outDir, "audit.tsv"), rows);
        File.WriteAllLines(Path.Combine(outDir, "colours.tsv"), colours);
        File.WriteAllLines(Path.Combine(outDir, "noscale.tsv"), NoScaleReport(scaleTrack));
        Assert.Null(failure);
    }

    // ---------------------------------------------------------------- the surfaces

    private static IEnumerable<(string Name, Func<double, Shot?> Take)> Surfaces()
    {
        // --- toasts: every severity, with and without an action, and the two OCR decisions.
        yield return ("toast-info", _ => FromForm(Toast("Nothing found", "No text in that region.", ToastSeverity.Info)));
        yield return ("toast-success", _ => FromForm(Toast("Saved", "shot-2026-09-17-141203.png", ToastSeverity.Success)));
        yield return ("toast-error", _ => FromForm(Toast("Save failed", "WinShot could not write to that folder. Choose a different folder in Settings.", ToastSeverity.Error)));
        yield return ("toast-action", _ => FromForm(Toast("Text copied", "https://example.com/a/rather/long/link", ToastSeverity.Success, onOpen: () => { })));
        yield return ("toast-long", _ => FromForm(Toast(
            "Couldn't reach the update server",
            "WinShot could not reach the update server, so it does not know whether a newer build exists. Your screenshots and recordings are unaffected.",
            ToastSeverity.Error)));
        yield return ("toast-ocr-links", _ => FromForm(Toast(
            "Text copied", "https://example.com", ToastSeverity.Success, onOpen: () => { },
            extras: new ToastExtras
            {
                Sticky = true,
                SecondaryText = "Don't open",
                CheckboxText = "Don't detect links",
            })));
        yield return ("toast-ocr-breaks", _ => FromForm(Toast(
            "Text copied", "Three lines of text were copied to the clipboard.", ToastSeverity.Success,
            extras: new ToastExtras
            {
                ToggleText = "Cut breaks",
                Toggle = () => Task.FromResult("Add breaks"),
            })));

        // --- first-run bubble: the three-row welcome card, laid out the way OnShown does.
        yield return ("welcome-bubble", _ => Welcome());

        // --- recording control bar: every state it holds, plus a hovered cell.
        yield return ("recording-bar", _ => Bar(paused: false, saving: false, hover: -1));
        yield return ("recording-bar-paused", _ => Bar(paused: true, saving: false, hover: -1));
        yield return ("recording-bar-saving", _ => Bar(paused: false, saving: true, hover: -1));
        yield return ("recording-bar-hover", _ => Bar(paused: false, saving: false, hover: 0));

        // --- recording options HUD.
        yield return ("recording-options", _ => FromForm(new FastRecordingOptionsDialog(new Settings())));

        // --- countdown ring: the first second and the last.
        yield return ("countdown-3", _ => Countdown(3, 3));
        yield return ("countdown-1", _ => Countdown(3, 1));

        // --- keystroke pill: one key, a chord, and a label long enough to ellipse.
        yield return ("keystroke-single", s => Keystroke("K", s));
        yield return ("keystroke-chord", s => Keystroke("Ctrl Shift F", s));
        yield return ("keystroke-long", s => Keystroke("Ctrl Alt Shift Win PrintScreen", s));

        // --- scrolling capture chrome: the pre-capture capsule and the running bar.
        yield return ("scroll-capsule", _ => Scroll(started: false));
        yield return ("scroll-running", _ => Scroll(started: true));

        // --- pin chrome: the chip bar over a capture, resting and with a chip hot.
        yield return ("pin-chrome", s => Pin(s, hot: -1));
        yield return ("pin-chrome-hover", s => Pin(s, hot: 0));

        // --- the shared dark menu: rows, gutter glyphs, separators, submenu arrow, accent hover.
        yield return ("menu-dark", _ => Menu(hoverRow: -1, submenu: false));
        yield return ("menu-dark-hover", _ => Menu(hoverRow: 4, submenu: false));
        yield return ("menu-dark-submenu", _ => Menu(hoverRow: -1, submenu: true));

        // --- the quick-actions card's own menu and one of its submenus. The CARD is pinned;
        // this is the menu, and it is here to be measured, never restyled.
        yield return ("card-menu", _ => CardMenu(hover: false, submenu: false));
        yield return ("card-menu-hover", _ => CardMenu(hover: true, submenu: false));
        yield return ("card-submenu", _ => CardMenu(hover: false, submenu: true));
    }

    private static ToastWindow Toast(
        string title, string body, ToastSeverity severity,
        Action? onOpen = null, ToastExtras? extras = null) =>
        new(title, body, anchorScreen: null, onOpen, severity, holdLong: false, extras);

    /// <summary>
    /// The first-run bubble. It lays its rows out in OnShown, which this harness never reaches:
    /// Show() would start the entrance tween, and a leaked Motion holder is a leaked
    /// timeBeginPeriod(1). Position() then LayoutRows() is exactly what OnShown does.
    /// Its header and subtitle are painted by the form rather than hosted in a Label, so those
    /// two boxes are declared here - the same rectangles OnPaint draws into.
    /// </summary>
    private static Shot? Welcome()
    {
        var bubble = new FirstRunWelcomeWindow(
            anchorScreen: null,
            new[]
            {
                new FirstRunWelcomeWindow.WelcomeAction(
                    "", "Capture a region", "Ctrl+Shift+1", () => { }),
                new FirstRunWelcomeWindow.WelcomeAction(
                    "", "Record the screen", "Ctrl+Shift+3", () => { }),
                new FirstRunWelcomeWindow.WelcomeAction(
                    "", "Extract text", "Ctrl+Shift+4", () => { }),
            },
            onDismissed: () => { });
        try
        {
            bubble.CreateControl();
            Invoke(bubble, "Position");
            Invoke(bubble, "LayoutRows");
            double s = ScaleOf(bubble);
            int P(int logical) => (int)Math.Round(logical * s);
            int width = bubble.ClientSize.Width;
            var boxes = new List<Extra>
            {
                new("welcome-title", "card",
                    new SD.Rectangle(P(14), P(10), width - P(28), P(26)), "WinShot is ready", true,
                    SD.ContentAlignment.MiddleLeft),
                new("welcome-subtitle", "card",
                    new SD.Rectangle(P(14), P(36), width - P(28), P(20)),
                    "Try one now, or find everything in the tray icon.", true,
                    SD.ContentAlignment.MiddleLeft),
            };
            // A WelcomeRow paints a glyph, a title and a hint itself, so the union of its ink
            // says nothing about any of the three. These are the row's own three rectangles.
            int r = 0;
            foreach (SD.Rectangle row in bubble.RowBoundsForTest)
            {
                string n = $"row{++r}";
                boxes.Add(new Extra($"{n}-glyph", "welcome-row",
                    new SD.Rectangle(row.X + P(10), row.Y + P(12), P(24), P(24)), string.Empty, false));
                boxes.Add(new Extra($"{n}-title", "welcome-row",
                    new SD.Rectangle(row.X + P(42), row.Y + P(4), row.Width - P(52), P(22)),
                    "title", true, SD.ContentAlignment.MiddleLeft));
                boxes.Add(new Extra($"{n}-hint", "welcome-row",
                    new SD.Rectangle(row.X + P(42), row.Y + P(26), row.Width - P(52), P(18)),
                    "hint", true, SD.ContentAlignment.MiddleLeft));
            }
            return FromForm(bubble, boxes.ToArray());
        }
        finally
        {
            bubble.Dispose();
        }
    }

    private static Shot? Bar(bool paused, bool saving, int hover)
    {
        var bar = new FastRecordingControlBar(new SD.Rectangle(0, 0, 900, 600), showTimer: true, stopHotkey: "Esc");
        try
        {
            bar.CreateControl();
            if (paused)
            {
                Field(bar, "_paused").SetValue(bar, true);
                Amount(bar, "_pause", 1d);
            }
            if (saving)
            {
                Field(bar, "_saving").SetValue(bar, true);
                Field(bar, "_saveSwap").SetValue(bar, 1d);
            }
            if (hover >= 0)
            {
                Field(bar, "_hoverIndex").SetValue(bar, hover);
                Wash(bar, "_hover", hover);
            }

            using SD.Bitmap image = (SD.Bitmap)typeof(FastRecordingControlBar)
                .GetMethod("RenderToBitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(bar, null)!;
            var cells = bar.Cells
                .Select(c => new Cell(c.Cell.ToString(), "bar", "Cell",
                    c.Cell == RecordingControlBarCell.Timer ? "00:00" : string.Empty,
                    c.Bounds,
                    false,
                    SD.ContentAlignment.MiddleCenter,
                    // The timer cell holds two things — a breathing dot and a numeral — so the
                    // union of its ink describes neither. Mid-save every cell has dissolved into
                    // the pill's own fill and what is left in its box is the spinner passing
                    // through.
                    saving || c.Cell == RecordingControlBarCell.Timer))
                .ToList();
            return new Shot((SD.Bitmap)image.Clone(), cells);
        }
        finally
        {
            bar.Dispose();
        }
    }

    private static Shot? Countdown(int total, int remaining)
    {
        var win = new FastRecordingCountdownWindow(total, new SD.Rectangle(0, 0, 900, 600));
        try
        {
            win.CreateControl();
            Field(win, "_remaining").SetValue(win, remaining);
            // The digit and the hint are painted by the form, so they are declared boxes rather
            // than controls — the same two rectangles OnPaint draws into.
            double s = ScaleOf(win);
            int P(int logical) => (int)Math.Round(logical * s);
            var extra = new[]
            {
                // The middle half of the badge: the digit is centred, and a box the full width of
                // the window crosses the ring on both sides.
                new Extra("countdown-digit", "badge",
                    new SD.Rectangle(win.ClientSize.Width / 4, P(10), win.ClientSize.Width / 2, P(120)),
                    remaining.ToString(), false),
                // The window's own arithmetic, not a copy of it: the hint's usable width is the
                // ring's chord at its height, and the audit has to measure the box the paint uses.
                // The paint's own box, pulled in by a pixel: the box is the ring's CHORD, which
                // is a straight line where the ring is a curve, so the arc still grazes the two
                // bottom corners of the rectangle.
                new Extra("countdown-hint", "badge",
                    SD.Rectangle.Inflate(HintBox(win.ClientSize, s), -P(3), 0),
                    "Click or Esc to cancel", true),
            };
            return FromForm(win, extra);
        }
        finally
        {
            win.Dispose();
        }
    }

    /// <summary>The countdown hint's box, from the window itself where it publishes one and
    /// from what it used to hard-code otherwise — the base has no such method, and a before/after
    /// run builds this harness against the base's src.</summary>
    private static SD.Rectangle HintBox(SD.Size client, double scale)
    {
        MethodInfo? bounds = typeof(FastRecordingCountdownWindow).GetMethod(
            "HintBounds", BindingFlags.Static | BindingFlags.NonPublic);
        if (bounds?.Invoke(null, new object[] { client, scale }) is SD.Rectangle box)
            return box;
        int Px(int logical) => (int)Math.Round(logical * scale);
        return new SD.Rectangle(0, client.Height - Px(42), client.Width, Px(24));
    }

    private static Shot? Keystroke(string run, double scale)
    {
        var pill = FastKeystrokeOverlayWindow.CreateForSmokeTest(new SD.Rectangle(0, 0, 1200, 700));
        try
        {
            // Reflected, and with a fallback, for one reason: a before/after run checks the base
            // out over src/ and the base has no such seam. The same goes for HintBounds below.
            MethodInfo? compose = typeof(FastKeystrokeOverlayWindow).GetMethod(
                "ComposePillForAudit", BindingFlags.Instance | BindingFlags.NonPublic);
            if (compose is null) return null;
            if (compose.Invoke(pill, new object[] { run }) is not SD.Bitmap frame) return null;
            // The pill's own text box, not the whole capsule: the capsule's rounded border is
            // chrome and measuring the chord against it says nothing about where the chord sits.
            int pad = (int)Math.Round(10 * scale);
            var cells = new List<Cell>
            {
                new("pill", "pill", "Pill", run,
                    new SD.Rectangle(pad, 0, Math.Max(1, frame.Width - (pad * 2)), frame.Height), false),
            };
            return new Shot(Flatten(frame, ThemePalette.WindowBg), cells);
        }
        finally
        {
            pill.Dispose();
        }
    }

    private static Shot? Scroll(bool started)
    {
        var bar = new ScrollControlsBar(new SD.Rectangle(100, 100, 900, 600));
        try
        {
            bar.CreateControl();
            if (started)
            {
                // Start() reveals the status line, the auto toggle and Done; the harness wants
                // the running bar as well as the pre-capture capsule.
                Invoke(bar, "Start");
                bar.PerformLayout();
            }
            return FromForm(bar);
        }
        finally
        {
            bar.Dispose();
        }
    }

    private static Shot? Pin(double scale, int hot)
    {
        using var capture = new SD.Bitmap(420, 280, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(capture))
            g.Clear(SD.Color.FromArgb(238, 244, 250));

        var settings = new SettingsService();
        settings.Current.PinnedRoundedCorners = true;
        settings.Current.PinnedShadow = false;
        var pin = new FastPinWindow(capture, settings);
        try
        {
            pin.CreateControl();
            Field(pin, "_scale").SetValue(pin, 1d);
            typeof(WF.Control).GetProperty("DeviceDpi", BindingFlags.Instance | BindingFlags.Public)!
                .GetSetMethod(true)?.Invoke(pin, new object[] { (int)Math.Round(96 * scale) });
            Invoke(pin, "ApplyScale");
            pin.ClientSize = new SD.Size(420, 280);
            Field(pin, "_mouseInside").SetValue(pin, true);
            Field(pin, "_chromeAlpha").SetValue(pin, 1d);
            if (hot >= 0) Field(pin, "_hoverButton").SetValue(pin, hot);

            using SD.Bitmap painted = PaintOnly(pin, SD.Color.Black);
            var cells = new List<Cell>();
            var bounds = typeof(FastPinWindow)
                .GetMethod("ButtonBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var visible = (int[])typeof(FastPinWindow)
                .GetProperty("VisibleButtons", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pin)!;
            foreach (int index in visible)
            {
                var rect = (SD.Rectangle)bounds.Invoke(pin, new object[] { index })!;
                if (rect.Width > 0)
                    cells.Add(new Cell($"chip{index}", "pin-bar", "Chip", string.Empty, rect, false));
            }
            var bar = (SD.Rectangle)Invoke(pin, "ToolbarBounds")!;
            var zoom = (SD.Rectangle)typeof(FastPinWindow)
                .GetMethod("ZoomChipBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, new object[] { bar })!;
            if (zoom.Width > 0)
                cells.Add(new Cell("zoom-chip", "pin-zoom", "Chip", "100%", zoom, false));
            return new Shot((SD.Bitmap)painted.Clone(), cells);
        }
        finally
        {
            pin.Dispose();
        }
    }

    /// <summary>
    /// The one dark menu every WinForms drop-down in the app shares
    /// (<see cref="DarkDropDown.DarkMenuRenderer"/>), built in the tray menu's shape: a disabled
    /// version header, separators between groups, a left gutter of glyphs on the capture verbs
    /// and none on the housekeeping rows, shortcut columns, and a submenu row. The tray menu
    /// itself is built inside App's instance constructor and cannot be reached without starting
    /// the application, so this is the same renderer and the same row vocabulary standing in for
    /// it — a defect in row geometry, hover inset, separator colour or arrow lands in both.
    /// </summary>
    private static Shot? Menu(int hoverRow, bool submenu)
    {
        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkDropDown.DarkMenuRenderer(),
            ShowImageMargin = true,
        };
        try
        {
            menu.Items.Add(new WF.ToolStripMenuItem("WinShot 1.0.0") { Enabled = false });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Row("Capture region", "Ctrl+Shift+1", ""));
            menu.Items.Add(Row("Capture window", "Ctrl+Shift+2", ""));
            menu.Items.Add(Row("Capture fullscreen", null, ""));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Row("Record screen", "Ctrl+Shift+3", ""));
            menu.Items.Add(Row("Capture text (OCR)", "Ctrl+Shift+4", ""));
            menu.Items.Add(Row("Scrolling capture", "Ctrl+Shift+5", ""));
            var recents = new WF.ToolStripMenuItem("Recent captures");
            recents.DropDownItems.Add(new WF.ToolStripMenuItem("shot-2026-09-17-141203.png"));
            recents.DropDownItems.Add(new WF.ToolStripMenuItem("shot-2026-09-17-140914.png"));
            menu.Items.Add(recents);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(Row("History…", "Ctrl+Shift+0", null));
            menu.Items.Add(Row("Settings…", null, null));
            menu.Items.Add(Row("Exit", null, null));
            ScaleGlyphs(menu);

            menu.Show(new SD.Point(0, 0));
            WF.Control target = menu;
            if (submenu)
            {
                recents.DropDown.Show(new SD.Point(0, 0));
                target = recents.DropDown;
            }
            else if (hoverRow >= 0 && menu.Items[hoverRow] is WF.ToolStripMenuItem hot)
            {
                hot.Select();
                menu.Refresh();
            }

            Shot? shot = FromStrip(target);
            if (submenu) recents.DropDown.Close();
            menu.Close();
            return shot;
        }
        finally
        {
            menu.Dispose();
        }

        static WF.ToolStripMenuItem Row(string text, string? shortcut, string? glyph) =>
            new(text) { ShortcutKeyDisplayString = shortcut, Tag = glyph };
    }

    /// <summary>The tray menu's own glyph rasteriser, through the same private method App uses,
    /// so this stands in for the tray gutter rather than inventing one.</summary>
    private static void ScaleGlyphs(WF.ContextMenuStrip menu)
    {
        int dpi = menu.DeviceDpi <= 0 ? 96 : menu.DeviceDpi;
        int size = WinShot.App.MenuGlyphPixels(dpi);
        menu.ImageScalingSize = new SD.Size(size, size);
        MethodInfo render = typeof(WinShot.App)
            .GetMethod("RenderMenuGlyph", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (WF.ToolStripItem item in menu.Items)
            if (item.Tag is string glyph && glyph.Length > 0)
                item.Image = render.Invoke(null, new object[] { glyph, dpi }) as SD.Image;
    }

    private static Shot? CardMenu(bool hover, bool submenu)
    {
        using var capture = new SD.Bitmap(640, 400, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(capture))
            g.Clear(SD.Color.FromArgb(46, 70, 110));

        var card = new FastQuickActionsWindow(capture, new SettingsService());
        try
        {
            var menu = (WF.ContextMenuStrip)typeof(FastQuickActionsWindow)
                .GetField("_overflowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(card)!;
            menu.Show(new SD.Point(0, 0));
            WF.Control target = menu;
            WF.ToolStripDropDown? opened = null;
            if (submenu)
            {
                foreach (WF.ToolStripItem item in menu.Items)
                {
                    if (item is not WF.ToolStripMenuItem { HasDropDownItems: true } parent) continue;
                    parent.DropDown.Show(new SD.Point(0, 0));
                    opened = parent.DropDown;
                    target = parent.DropDown;
                    break;
                }
                if (opened is null) return null;
            }
            else if (hover)
            {
                WF.ToolStripMenuItem? row = menu.Items.OfType<WF.ToolStripMenuItem>()
                    .FirstOrDefault(i => i.Enabled && !i.HasDropDownItems);
                row?.Select();
                menu.Refresh();
            }

            Shot? shot = FromStrip(target);
            opened?.Close();
            menu.Close();
            return shot;
        }
        finally
        {
            card.Dispose();
        }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// One thing to measure. <paramref name="BoxIsTheControl"/> is a checkbox, whose box defines
    /// the control's height and hugs its left edge — clipping and lopsidedness mean nothing there.
    /// <paramref name="Align"/> is where the content was ASKED to sit: "the label is not centred"
    /// is the design on a left-aligned row, not a defect, and flagging it buried every real
    /// finding under four hundred rows of noise. <paramref name="Composite"/> marks a box that
    /// holds several things at once — a container, or a row that paints a glyph, a title and a
    /// hint — where the union of the ink says nothing about any of them; those are measured
    /// through their own declared sub-boxes instead.
    /// </summary>
    private sealed record Cell(
        string Id, string Row, string Type, string Text, SD.Rectangle Box, bool BoxIsTheControl,
        SD.ContentAlignment Align = SD.ContentAlignment.MiddleCenter, bool Composite = false);

    private sealed record Extra(
        string Id, string Row, SD.Rectangle Box, string Text, bool IsText,
        SD.ContentAlignment Align = SD.ContentAlignment.MiddleCenter);

    private sealed class Shot(SD.Bitmap image, List<Cell> cells) : IDisposable
    {
        public SD.Bitmap Image { get; } = image;
        public List<Cell> Cells { get; } = cells;
        public void Dispose() => Image.Dispose();
    }

    private static Shot? FromForm(WF.Form form, Extra[]? extra = null)
    {
        form.CreateControl();
        if (form.ClientSize.Width < 2 || form.ClientSize.Height < 2) return null;

        SD.Bitmap image = Composite(form);

        var cells = new List<Cell>();
        Walk(form, form, cells);
        if (extra is not null)
            foreach (Extra e in extra)
                cells.Add(new Cell(
                    e.Id, e.Row, "Painted", e.IsText ? e.Text : string.Empty, e.Box, false, e.Align));
        return new Shot(image, cells);
    }

    /// <summary>
    /// The form and everything in it, painted into one frame.
    ///
    /// Not <c>Form.DrawToBitmap</c>: a form that has never been shown is not Visible, so
    /// <c>CreateControl</c> declines to build its children's window handles and WM_PRINT's
    /// PRF_CHILDREN then has no child windows to walk — every toast came back an empty card.
    /// Showing the form would fix that and would also start its entrance tween, and a Motion
    /// holder leaked out of a test is a leaked timeBeginPeriod(1).
    ///
    /// So each control paints itself, through its own OnPaint, into its OWN bitmap at the origin,
    /// and the bitmaps are stacked back to front. Its own bitmap rather than one shared Graphics
    /// under a translate: <c>TextRenderer</c> is GDI and GDI ignores a GDI+ transform, so every
    /// label on the card landed in the top-left corner of the window instead of inside its own
    /// button. (CountdownRing carries the same warning in the other direction.)
    ///
    /// 24bpp throughout: TextRenderer writes no alpha channel, and text drawn into a fresh
    /// 32bppArgb surface composites as nothing at all.
    /// </summary>
    private static SD.Bitmap Composite(WF.Form form) => RenderControl(form, form.BackColor);

    private static SD.Bitmap RenderControl(WF.Control control, SD.Color plate)
    {
        SD.Color ground = control.BackColor.A == 255 ? control.BackColor : plate;
        var frame = new SD.Bitmap(
            Math.Max(1, control.Width), Math.Max(1, control.Height),
            SD.Imaging.PixelFormat.Format24bppRgb);
        using (SD.Graphics g = SD.Graphics.FromImage(frame))
        {
            g.Clear(ground);
            Paint(g, control);
            // Back to front: index 0 is the top of a WinForms z-order.
            for (int i = control.Controls.Count - 1; i >= 0; i--)
            {
                WF.Control child = control.Controls[i];
                if (!SelfVisible(child) || child.Width < 1 || child.Height < 1) continue;
                using SD.Bitmap sub = RenderControl(child, ground);
                g.DrawImageUnscaled(sub, child.Location);
            }
        }
        return frame;
    }

    private static void Paint(SD.Graphics g, WF.Control control)
    {
        MethodInfo? paint = null;
        for (Type? t = control.GetType(); t is not null && paint is null; t = t.BaseType)
            paint = t.GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(WF.PaintEventArgs) }, null);
        if (paint is null) return;
        using var args = new WF.PaintEventArgs(
            g, new SD.Rectangle(0, 0, control.Width, control.Height));
        paint.Invoke(control, new object[] { args });
    }

    /// <summary>A control's client-coordinate box plus every child's, so a cell is measured
    /// against the plate it is actually painted on.</summary>
    private static void Walk(WF.Control root, WF.Control node, List<Cell> into)
    {
        foreach (WF.Control child in node.Controls)
        {
            if (!SelfVisible(child) || child.Width < 6 || child.Height < 6)
            {
                Walk(root, child, into);
                continue;
            }
            SD.Point tl = Offset(root, child);
            into.Add(new Cell(
                string.IsNullOrEmpty(child.Name) ? Id(child) : child.Name,
                string.IsNullOrEmpty(node.Name) ? node.GetType().Name : node.Name,
                child.GetType().Name,
                child.Text ?? string.Empty,
                new SD.Rectangle(tl, child.Size),
                child is WF.CheckBox or WF.RadioButton,
                AlignmentOf(child),
                // A control that paints several things at once — a welcome row's glyph, title and
                // hint; a segmented switch's cells — has no single ink box worth judging. Those
                // are measured through their own declared sub-boxes instead.
                child.Controls.Count > 0 || child is WF.Panel or WF.TableLayoutPanel
                    or DarkSegmented or DarkDropDown || child.GetType().Name == "WelcomeRow"));
            Walk(root, child, into);
        }
    }

    /// <summary>
    /// Whether a control was ASKED to be visible, rather than whether it can be seen.
    /// <c>Control.Visible</c> walks up the parent chain and answers false for every child of a
    /// form that has not been shown, which would be every control this harness wants to measure.
    /// The distinction is load-bearing: the scrolling bar hides three of its five controls before
    /// capture starts, and those must stay hidden here too.
    /// </summary>
    private const int StateVisible = 0x00000002;

    private static readonly MethodInfo GetStateMethod = typeof(WF.Control)
        .GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool SelfVisible(WF.Control c) =>
        (bool)GetStateMethod.Invoke(c, new object[] { StateVisible })!;

    /// <summary>A control's origin in the form's client pixels, summed up the parent chain —
    /// screen coordinates would need a window that has actually been placed on one.</summary>
    private static SD.Point Offset(WF.Control root, WF.Control control)
    {
        int x = 0, y = 0;
        for (WF.Control? n = control; n is not null && !ReferenceEquals(n, root); n = n.Parent)
        {
            x += n.Left;
            y += n.Top;
        }
        return new SD.Point(x, y);
    }

    /// <summary>Where a control was asked to put its content. A DarkDropDown and a menu row are
    /// left-aligned by construction; a Label says so itself.</summary>
    private static SD.ContentAlignment AlignmentOf(WF.Control c) => c switch
    {
        WF.Label l => l.TextAlign,
        DarkButton b => b.TextAlign,
        DarkDropDown or DarkCheckBox => SD.ContentAlignment.MiddleLeft,
        _ => SD.ContentAlignment.MiddleCenter,
    };

    private static bool LeftOrRight(SD.ContentAlignment a) => a is
        SD.ContentAlignment.TopLeft or SD.ContentAlignment.MiddleLeft or SD.ContentAlignment.BottomLeft
        or SD.ContentAlignment.TopRight or SD.ContentAlignment.MiddleRight or SD.ContentAlignment.BottomRight;

    private static Shot? FromStrip(WF.Control strip)
    {
        if (strip.Width < 2 || strip.Height < 2) return null;
        var image = new SD.Bitmap(strip.Width, strip.Height, SD.Imaging.PixelFormat.Format24bppRgb);
        strip.DrawToBitmap(image, new SD.Rectangle(SD.Point.Empty, image.Size));

        var cells = new List<Cell>();
        if (strip is WF.ToolStrip ts)
        {
            foreach (WF.ToolStripItem item in ts.Items)
            {
                if (item.Height < 4 || item.Width < 6) continue;
                cells.Add(new Cell(
                    Slug(item.Text) is { Length: > 0 } s ? s : item.GetType().Name,
                    "menu",
                    item.GetType().Name,
                    item.Text ?? string.Empty,
                    item.Bounds,
                    false,
                    SD.ContentAlignment.MiddleLeft));
            }
        }
        return new Shot(image, cells);
    }

    private static SD.Bitmap PaintOnly(WF.Form form, SD.Color ground)
    {
        var bitmap = new SD.Bitmap(
            Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height),
            SD.Imaging.PixelFormat.Format32bppArgb);
        using SD.Graphics g = SD.Graphics.FromImage(bitmap);
        g.Clear(ground);
        using var args = new WF.PaintEventArgs(g, new SD.Rectangle(SD.Point.Empty, bitmap.Size));
        form.GetType()
            .GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { args });
        return bitmap;
    }

    /// <summary>A per-pixel-alpha frame over an opaque ground — what a person actually sees.
    /// Measuring one against transparency reads back the compositing, not the pixels.</summary>
    private static SD.Bitmap Flatten(SD.Bitmap frame, SD.Color ground)
    {
        var flat = new SD.Bitmap(frame.Width, frame.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(flat))
        {
            g.Clear(ground);
            g.DrawImageUnscaled(frame, 0, 0);
        }
        frame.Dispose();
        return flat;
    }

    // ---------------------------------------------------------------- measuring

    private static void Measure(
        Shot shot, string surface, int dpi, double scale, string cropDir,
        List<string> rows, Dictionary<string, Dictionary<int, (int, int)>> track)
    {
        SD.Bitmap image = shot.Image;
        byte[] px = Pixels(image, out int stride);
        double dev = dpi / 96.0;

        var measured = new List<(Cell Cell, SD.Rectangle Ink, SD.Color InkColour, SD.Color Bg, double Stroke)>();
        foreach (Cell cell in shot.Cells)
        {
            SD.Rectangle box = SD.Rectangle.Intersect(cell.Box, new SD.Rectangle(0, 0, image.Width, image.Height));
            if (box.Width < 4 || box.Height < 4) continue;
            SD.Color bg = Modal(px, stride, box);
            bool[,] mask = InkMask(px, stride, box, bg);
            // Chrome first: Suppress strikes out near-full columns, which can cut the surface's
            // own outline into fragments that no longer reach the edge for the flood to find.
            DropChrome(mask, box, image.Width, image.Height);
            Suppress(mask, box, Math.Max(2, (int)Math.Ceiling(2 * dev)));
            SD.Rectangle ink = Bounds(mask, box);
            if (ink.IsEmpty) continue;
            SD.Color inkColour = Brightest(px, stride, box, mask);
            double stroke = Stroke(mask, box);
            ink.Offset(-box.X, -box.Y);
            measured.Add((cell with { Box = box }, ink, inkColour, bg, stroke));
        }

        var iconRows = measured
            .Where(m => m.Cell.Text.Length == 0 && !m.Cell.Composite)
            .GroupBy(m => m.Cell.Row)
            .ToList();
        var rowMedian = iconRows.ToDictionary(g => g.Key, g => Median(g.Select(m => (double)m.Ink.Height).ToList()));
        var rowCount = iconRows.ToDictionary(g => g.Key, g => g.Count());

        foreach (var (cell, ink, inkColour, bg, stroke) in measured)
        {
            int cw = cell.Box.Width, ch = cell.Box.Height;
            int gl = ink.Left, gt = ink.Top, gr = cw - ink.Right, gb = ch - ink.Bottom;
            double pct = ch == 0 ? 0 : 100.0 * ink.Height / ch;
            bool isText = cell.Text.Length > 0;
            double contrast = Contrast(inkColour, bg);

            var flags = new List<string>();
            // A cell whose ink fills its own box in BOTH axes is not a label sitting in a box —
            // it is the box's own chrome: the capsule cap the end cells of the recording pill
            // reach into, the ring a default button wears, the pill's border, the countdown's own
            // circle. Nothing about clipping or centring can be read off it, so it is called what
            // it is and not flagged as four defects it does not have.
            bool frame = ink.Width >= cw - (2 * dev) && ink.Height >= ch - (2 * dev);
            if (frame) flags.Add("FRAME");
            bool judged = !cell.BoxIsTheControl && !cell.Composite && !frame;
            if (judged)
            {
                if (gt < 2 * dev || gb < 2 * dev) flags.Add("CLIPPED-V");
                if (gl < 2 * dev || gr < 2 * dev) flags.Add("CLIPPED-H");
                // Only where the content was asked to be centred: a left-aligned label in a wide
                // box is lopsided on purpose, and that is most of the labels in the app.
                if (!LeftOrRight(cell.Align) && Math.Abs(gl - gr) > 2 * dev) flags.Add("LOPSIDED-H");
                double vTol = isText ? (2 * dev) + (0.25 * ink.Height) : Math.Max(1, dev);
                if (Math.Abs(gt - gb) > vTol) flags.Add("OFFCENTRE-V");
                if (isText && pct < 26) flags.Add("TINY");
            }
            if (!isText && !cell.Composite && rowMedian.TryGetValue(cell.Row, out double med) && med > 0
                && rowCount[cell.Row] >= 4 && Math.Abs(ink.Height - med) / med > 0.25)
                flags.Add($"ODD-SIZE({ink.Height}v{med:F0})");
            if (contrast > 0 && contrast < (isText ? 4.5 : 3.0)) flags.Add($"LOWCONTRAST({contrast:F2})");

            string key = $"{surface}\t{cell.Id}\t{cell.Row}\t{(isText ? "text" : "icon")}";
            if (!track.TryGetValue(key, out var byDpi)) track[key] = byDpi = new Dictionary<int, (int, int)>();
            byDpi[dpi] = (ink.Height, ch);

            rows.Add(string.Join("\t",
                surface, dpi, cell.Id, cell.Type, Flat(isText ? cell.Text : "(icon)"), cell.Row,
                cell.Box.X, cell.Box.Y, cw, ch, ink.Width, ink.Height, gl, gt, gr, gb,
                pct.ToString("F0", CultureInfo.InvariantCulture),
                stroke.ToString("F2", CultureInfo.InvariantCulture),
                Hex(inkColour), Hex(bg), contrast.ToString("F2", CultureInfo.InvariantCulture),
                flags.Count == 0 ? "ok" : string.Join("+", flags)));

            if (flags.Count > 0)
                Crop4x(image, cell.Box, Path.Combine(cropDir, $"{surface}-{Slug(cell.Id)}-{dpi}-4x.png"));
        }
        _ = scale;
    }

    /// <summary>
    /// The cross-scale pass, and the one this harness exists for. A hand-scaled WinForms surface
    /// multiplies every box by its monitor's scale, but a <c>GraphicsUnit.Point</c> font resolves
    /// its pixel size once, against the DC it was constructed on — so the box grows and the type
    /// in it does not. At 200 % the label is half the size the layout was drawn for. This reports
    /// every cell whose box roughly doubled from 96 to 192 while its ink stayed put.
    /// </summary>
    private static IEnumerable<string> NoScaleReport(Dictionary<string, Dictionary<int, (int InkH, int BoxH)>> track)
    {
        yield return "surface\tcell\trow\tkind\tbox_96\tbox_192\tink_96\tink_192\tbox_growth\tink_growth\tverdict";
        foreach ((string key, var byDpi) in track.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!byDpi.TryGetValue(96, out var lo) || !byDpi.TryGetValue(192, out var hi)) continue;
            if (lo.BoxH <= 0 || lo.InkH <= 0) continue;
            double boxGrowth = hi.BoxH / (double)lo.BoxH;
            double inkGrowth = hi.InkH / (double)lo.InkH;
            // The box doubled (or nearly) and the ink did not follow it up by even half as much.
            string verdict = boxGrowth > 1.6 && inkGrowth < 1.3 ? "NOSCALE"
                : boxGrowth > 1.6 && inkGrowth < 1.7 ? "UNDERSCALED"
                : "ok";
            yield return string.Join("\t", key, lo.BoxH, hi.BoxH, lo.InkH, hi.InkH,
                boxGrowth.ToString("F2", CultureInfo.InvariantCulture),
                inkGrowth.ToString("F2", CultureInfo.InvariantCulture), verdict);
        }
    }

    /// <summary>
    /// Every colour a surface paints, against the palette. A surface that paints a grey nobody
    /// declared is a surface somebody typed a number into, and the app has three such greys.
    /// </summary>
    private static void Census(SD.Bitmap image, string surface, int dpi, List<string> into)
    {
        byte[] px = Pixels(image, out int stride);
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int i = (y * stride) + (x * 4);
                int rgb = (px[i + 2] << 16) | (px[i + 1] << 8) | px[i];
                counts[rgb] = counts.TryGetValue(rgb, out int n) ? n + 1 : 1;
            }

        double total = image.Width * (double)image.Height;
        foreach ((int rgb, int n) in counts.OrderByDescending(kv => kv.Value).Take(24))
        {
            double share = 100.0 * n / total;
            if (share < 0.25) break;
            var colour = SD.Color.FromArgb(rgb | unchecked((int)0xFF000000));
            into.Add(string.Join("\t", surface, dpi, Hex(colour), n,
                share.ToString("F2", CultureInfo.InvariantCulture), TokenFor(colour)));
        }
    }

    /// <summary>The palette name of a colour, or "off-token" — with a tolerance, because a
    /// hairline or a wash composites onto whatever is under it.</summary>
    private static string TokenFor(SD.Color c)
    {
        foreach ((string name, SD.Color token) in Tokens)
            if (Math.Abs(c.R - token.R) <= 2 && Math.Abs(c.G - token.G) <= 2 && Math.Abs(c.B - token.B) <= 2)
                return name;
        return "off-token";
    }

    private static readonly (string Name, SD.Color Colour)[] Tokens = BuildTokens();

    private static (string, SD.Color)[] BuildTokens()
    {
        var list = new List<(string, SD.Color)>();
        foreach (FieldInfo f in typeof(ThemePalette)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(SD.Color)))
            list.Add((f.Name, (SD.Color)f.GetValue(null)!));
        list.Add(("White", SD.Color.White));
        list.Add(("Black", SD.Color.Black));
        // The washes and hairlines are alpha layers; record what they composite to over the two
        // plates they are ever laid on, so "off-token" means nobody declared it at all.
        foreach (SD.Color under in new[] { ThemePalette.ToolbarBg, ThemePalette.WindowBg, ThemePalette.Elevated, ThemePalette.SurfaceAlt })
            foreach ((string name, SD.Color over) in new[]
                     {
                         ("HoverFill", ThemePalette.HoverFill),
                         ("Border", ThemePalette.Border),
                         ("BorderStrong", ThemePalette.BorderStrong),
                     })
                list.Add(($"{name}/{Hex(under)}", ThemePalette.Over(over, under)));
        return list.ToArray();
    }

    // ---------------------------------------------------------------- pixel plumbing

    private static byte[] Pixels(SD.Bitmap bmp, out int stride)
    {
        SD.Imaging.BitmapData data = bmp.LockBits(
            new SD.Rectangle(0, 0, bmp.Width, bmp.Height),
            SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            stride = Math.Abs(data.Stride);
            var buf = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * data.Stride), buf, y * stride, stride);
            return buf;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static SD.Color Modal(byte[] px, int stride, SD.Rectangle box)
    {
        var counts = new Dictionary<int, int>();
        for (int y = box.Top; y < box.Bottom; y++)
            for (int x = box.Left; x < box.Right; x++)
            {
                int i = (y * stride) + (x * 4);
                int rgb = (px[i + 2] << 16) | (px[i + 1] << 8) | px[i];
                counts[rgb] = counts.TryGetValue(rgb, out int n) ? n + 1 : 1;
            }
        int best = counts.OrderByDescending(kv => kv.Value).First().Key;
        return SD.Color.FromArgb(unchecked((int)0xFF000000) | best);
    }

    private static bool[,] InkMask(byte[] px, int stride, SD.Rectangle box, SD.Color bg)
    {
        double floor = Luma(bg.R, bg.G, bg.B) + InkStep;
        var mask = new bool[box.Width, box.Height];
        for (int y = 0; y < box.Height; y++)
            for (int x = 0; x < box.Width; x++)
            {
                int i = ((y + box.Top) * stride) + ((x + box.Left) * 4);
                mask[x, y] = Luma(px[i + 2], px[i + 1], px[i]) > floor;
            }
        return mask;
    }

    /// <summary>
    /// Strikes out rules: the hairline between two cells of the recording pill, a card's border,
    /// the edge of a chip's plate. A rule is a run of at most <paramref name="pen"/> adjacent
    /// columns each of which is ink down more than half the cell — no glyph in this app draws an
    /// unbroken half-height stroke two pixels wide and nothing else beside it. Without this every
    /// cell of the recording bar measured as ink from its own left edge, because the divider
    /// drawn ON that edge is ink.
    /// </summary>
    private static void Suppress(bool[,] mask, SD.Rectangle box, int pen)
    {
        Strike(box.Width, box.Height, pen, (i, j) => mask[i, j], (i, j) => mask[i, j] = false);
        Strike(box.Height, box.Width, pen, (i, j) => mask[j, i], (i, j) => mask[j, i] = false);
    }

    private static void Strike(int lines, int depth, int pen, Func<int, int, bool> read, Action<int, int> clear)
    {
        var full = new bool[lines];
        for (int i = 0; i < lines; i++)
        {
            int run = 0;
            for (int j = 0; j < depth; j++) if (read(i, j)) run++;
            full[i] = run >= 0.5 * depth;
        }
        for (int i = 0; i < lines; i++)
        {
            if (!full[i]) continue;
            int end = i;
            while (end + 1 < lines && full[end + 1]) end++;
            if (end - i + 1 <= pen)
                for (int k = i; k <= end; k++)
                    for (int j = 0; j < depth; j++) clear(k, j);
            i = end;
        }
    }

    /// <summary>
    /// Removes the surface's own outline from a cell that touches it. The recording pill is a
    /// capsule and its end cells reach into the rounded caps, so the cap's arc — bright, and
    /// inside the cell's box — was being measured as that cell's glyph and made every end cell
    /// report ink from one edge of itself to the other. Any run of ink CONNECTED to a cell edge
    /// that is also the surface's edge is the surface's own chrome, never a glyph: nothing in
    /// this app draws a label that runs off the side of the window it is in.
    /// </summary>
    private static void DropChrome(bool[,] mask, SD.Rectangle box, int surfaceW, int surfaceH)
    {
        var stack = new Stack<(int X, int Y)>();
        void Seed(int x, int y) { if (mask[x, y]) stack.Push((x, y)); }

        if (box.Left == 0) for (int y = 0; y < box.Height; y++) Seed(0, y);
        if (box.Right >= surfaceW) for (int y = 0; y < box.Height; y++) Seed(box.Width - 1, y);
        if (box.Top == 0) for (int x = 0; x < box.Width; x++) Seed(x, 0);
        if (box.Bottom >= surfaceH) for (int x = 0; x < box.Width; x++) Seed(x, box.Height - 1);

        while (stack.Count > 0)
        {
            (int x, int y) = stack.Pop();
            if (x < 0 || y < 0 || x >= box.Width || y >= box.Height || !mask[x, y]) continue;
            mask[x, y] = false;
            stack.Push((x - 1, y));
            stack.Push((x + 1, y));
            stack.Push((x, y - 1));
            stack.Push((x, y + 1));
        }
    }

    private static SD.Rectangle Bounds(bool[,] mask, SD.Rectangle box)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < box.Height; y++)
            for (int x = 0; x < box.Width; x++)
            {
                if (!mask[x, y]) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        return maxX < 0
            ? SD.Rectangle.Empty
            : SD.Rectangle.FromLTRB(box.X + minX, box.Y + minY, box.X + maxX + 1, box.Y + maxY + 1);
    }

    /// <summary>The brightest colour inside the ink — the ink's own colour, for the contrast
    /// reading. Anti-aliased edges are dimmer than the glyph's core, so the maximum is the
    /// colour the type was asked for rather than a blend of it with its plate.</summary>
    private static SD.Color Brightest(byte[] px, int stride, SD.Rectangle box, bool[,] mask)
    {
        double best = -1;
        SD.Color colour = SD.Color.Empty;
        for (int y = 0; y < box.Height; y++)
            for (int x = 0; x < box.Width; x++)
            {
                if (!mask[x, y]) continue;
                int i = ((y + box.Top) * stride) + ((x + box.Left) * 4);
                double l = Luma(px[i + 2], px[i + 1], px[i]);
                if (l <= best) continue;
                best = l;
                colour = SD.Color.FromArgb(255, px[i + 2], px[i + 1], px[i]);
            }
        return colour;
    }

    private static double Stroke(bool[,] mask, SD.Rectangle box)
    {
        var runs = new List<int>();
        for (int y = 0; y < box.Height; y++)
        {
            int run = 0;
            for (int x = 0; x < box.Width; x++)
            {
                if (mask[x, y]) { run++; continue; }
                if (run is > 0 and <= 12) runs.Add(run);
                run = 0;
            }
            if (run is > 0 and <= 12) runs.Add(run);
        }
        if (runs.Count == 0) return 0;
        runs.Sort();
        return runs[runs.Count / 2];
    }

    private static double Luma(int r, int g, int b) => (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    private static double Contrast(SD.Color a, SD.Color b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
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

    /// <summary>
    /// A 4x nearest-neighbour blow-up of one cell with four pixels of magenta around its box, so
    /// ink touching magenta is ink outside the control. Nearest-neighbour deliberately: these
    /// are device pixels and the question is what they are, not what they would look like
    /// smoothed.
    /// </summary>
    private static void Crop4x(SD.Bitmap image, SD.Rectangle box, string path)
    {
        const int Z = 4, Pad = 4;
        SD.Rectangle src = SD.Rectangle.Intersect(
            SD.Rectangle.Inflate(box, Pad, Pad), new SD.Rectangle(0, 0, image.Width, image.Height));
        if (src.Width <= 0 || src.Height <= 0) return;

        using var crop = new SD.Bitmap(src.Width * Z, src.Height * Z, SD.Imaging.PixelFormat.Format32bppArgb);
        using (SD.Graphics g = SD.Graphics.FromImage(crop))
        {
            g.Clear(SD.Color.Magenta);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(image, new SD.Rectangle(0, 0, crop.Width, crop.Height), src, SD.GraphicsUnit.Pixel);
            // The box itself, back in magenta, so the layout edge is visible over the ink.
            using var pen = new SD.Pen(SD.Color.Magenta, 1);
            var edge = new SD.Rectangle(
                (box.X - src.X) * Z, (box.Y - src.Y) * Z, (box.Width * Z) - 1, (box.Height * Z) - 1);
            g.DrawRectangle(pen, edge);
        }
        crop.Save(path, SD.Imaging.ImageFormat.Png);
    }

    // ---------------------------------------------------------------- reflection helpers

    private static FieldInfo Field(object o, string name)
    {
        for (Type? t = o.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } f)
                return f;
        throw new MissingFieldException(o.GetType().Name, name);
    }

    private static object? Invoke(object o, string name)
    {
        for (Type? t = o.GetType(); t is not null; t = t.BaseType)
            if (t.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } m)
                return m.Invoke(o, null);
        throw new MissingMethodException(o.GetType().Name, name);
    }

    private static double ScaleOf(object o) => (double)Field(o, "_scale").GetValue(o)!;

    /// <summary>Lands a <c>StateAmount</c> at a value without a tween — a tween would hold the
    /// process-wide high-resolution clock for a control nobody is looking at.</summary>
    private static void Amount(object owner, string field, double value)
    {
        object amount = Field(owner, field).GetValue(owner)!;
        amount.GetType().GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(amount, new object[] { value });
    }

    /// <summary>Same, for a <c>HoverWash</c>: hand it the cell, then land the rising amount.</summary>
    private static void Wash(object owner, string field, int cell)
    {
        object wash = Field(owner, field).GetValue(owner)!;
        wash.GetType().GetMethod("To", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(wash, new object?[] { cell });
        object rising = wash.GetType()
            .GetField("_rising", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(wash)!;
        rising.GetType().GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(rising, new object[] { 1d });
    }

    private static string Id(WF.Control c) =>
        new string((c.Text ?? string.Empty).Where(char.IsLetterOrDigit).Take(14).ToArray())
            is { Length: > 0 } s ? s : c.GetType().Name;

    private static string Slug(string? s) =>
        new((s ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(28).ToArray());

    private static string Hex(SD.Color c) => c.IsEmpty ? "-" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Flat(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[v.Count / 2];
    }
}
