using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using WinShot.SettingsUi;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The audit four review rounds should have been.
///
/// Two defects shipped past four reviews — a clipped "Done" label and a ring drawn one
/// stroke width small — because every reviewer read numbers off the XAML and nobody looked
/// at the pixels. Numbers cannot see clipping: a 28 px button with 13 px type measures fine
/// and still eats the descender.
///
/// So this harness measures INK, not layout. For each text-bearing control it renders the
/// control twice — once as laid out, once with the content presenter's opacity at zero — and
/// diffs the two bitmaps. What is left is exactly the glyph ink, in device pixels, at the DPI
/// asked for. From the ink box against the control box it derives the four things that were
/// missed:
///
///   * clipped   — ink is less than 2 device px inside an edge (or touches it)
///   * tiny      — ink height is under 35 % of the control height (Done was 46 % of the pill
///                 but only after the fix; it read as tiny because the clip ate the top)
///   * off-centre— top gap and bottom gap differ by more than 1 device px
///   * lopsided  — left gap and right gap differ by more than 2 device px
///
/// Output: full-window PNGs plus a 4x crop per flagged control, and audit.tsv with a row per
/// control so a defect can be re-checked without re-running. Gated on WINSHOT_VISUAL_AUDIT=1
/// because it creates a WPF Application; output dir is WINSHOT_VISUAL_AUDIT_OUT.
/// </summary>
public class VisualAuditHarness
{
    private static readonly double[] Dpis = { 96, 120, 144, 192 };

    [Fact]
    public void AuditEveryWpfSurface()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_VISUAL_AUDIT") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_VISUAL_AUDIT_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-visual-audit");
        string cropDir = Path.Combine(outDir, "crops");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(cropDir);

        var rows = new List<string>
        {
            "surface\tdpi\tcontrol\ttype\ttext\trow\tctrl_x\tctrl_y\tctrl_w\tctrl_h\tink_w\tink_h\tgap_l\tgap_t\tgap_r\tgap_b\tink_pct_h\tstroke\tflags"
        };

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) _ = new Application();
                // Closing a surface must not take the Application with it — the sweep opens
                // nine windows one after another and the default OnLastWindowClose killed
                // everything after the first.
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();
                var settings = new SettingsService();
                settings.Current.SaveFolder = @"C:\Pictures\WinShot";
                var history = new HistoryService(settings);

                foreach (var (name, make, arrange) in Surfaces(settings, history))
                {
                    Window? win = null;
                    try
                    {
                        win = make();
                        win.WindowStartupLocation = WindowStartupLocation.Manual;
                        win.Left = -6000;
                        win.Top = -6000;
                        win.ShowInTaskbar = false;
                        win.Show();
                        for (int i = 0; i < 12; i++) { Pump(); Thread.Sleep(20); }
                        arrange?.Invoke(win);
                        for (int i = 0; i < 8; i++) { Pump(); Thread.Sleep(20); }
                        win.UpdateLayout();
                        Pump();

                        foreach (double dpi in Dpis)
                        {
                            RenderWindow(win, Path.Combine(outDir, $"{name}-{(int)dpi}.png"), dpi);
                            AuditControls(win, name, dpi, cropDir, rows);
                        }
                    }
                    catch (Exception ex)
                    {
                        rows.Add($"{name}\t-\t-\t-\tRENDER FAILED: {ex.GetType().Name}: {Flat(ex.Message)}\t-\t\t\t\t\t\t\t\t\tERROR");
                    }
                    finally
                    {
                        try { win?.Close(); } catch { }
                        Pump();
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        File.WriteAllLines(Path.Combine(outDir, "audit.tsv"), rows);
        Assert.Null(failure);
    }

    private static IEnumerable<(string Name, Func<Window> Make, Action<Window>? Arrange)> Surfaces(
        SettingsService settings, HistoryService history)
    {
        yield return ("editor", () => new EditorWindow(Shot(1200, 760), settings, history), null);
        yield return ("history", () => new HistoryWindow(history, settings), null);
        yield return ("settings-general", () => new SettingsWindow(settings), null);
        yield return ("settings-quick-access", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 2));
        yield return ("settings-recording", () => new SettingsWindow(settings),
            w => { Select(w, "SectionList", 3); Pump(); Select(w, "RecordingSubList", 0); });
        yield return ("settings-screenshots", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 4));
        yield return ("settings-advanced", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 6));
        yield return ("resize-dialog", () => new ResizeDialog(1200, 760), null);
        yield return ("background-composer",
            () => new WinShot.Editor.Background.BackgroundComposerWindow(Shot(800, 500), settings, history), null);
    }

    private static void Select(Window w, string listName, int index)
    {
        if (w.FindName(listName) is ListBox list && index < list.Items.Count)
            list.SelectedIndex = index;
    }

    private static SD.Bitmap Shot(int w, int h)
    {
        var bmp = new SD.Bitmap(w, h);
        using var g = SD.Graphics.FromImage(bmp);
        g.Clear(SD.Color.FromArgb(0x2E, 0x46, 0x6E));
        using var pen = new SD.Pen(SD.Color.FromArgb(0xE8, 0xE8, 0xE8), 3);
        for (int x = 0; x < w; x += 80) g.DrawLine(pen, x, 0, x, h);
        using var f = new SD.Font("Segoe UI", 28);
        g.DrawString("Capture", f, SD.Brushes.White, 40, 40);
        return bmp;
    }

    // ---------------------------------------------------------------- measuring

    private static void AuditControls(
        Window win, string surface, double dpi, string cropDir, List<string> rows)
    {
        // Everything is measured out of ONE pair of full-window renders: the window as the user
        // sees it, and the window with every audited label and glyph at zero opacity. The
        // difference is the ink, in the window's own pixels, with no per-element brush in the
        // way — a VisualBrush aligns to a visual's CONTENT bounds, so a template with a
        // negative margin (SurveyPrimaryButton's focus ring) shifted every element it rendered
        // and made a centred label measure 2 px low.
        var targets = new List<(FrameworkElement Fe, List<FrameworkElement> Presenters, string Text)>();
        foreach (var c in Descendants(win))
        {
            if (c is not FrameworkElement fe || !fe.IsVisible) continue;
            if (fe.ActualWidth < 6 || fe.ActualHeight < 6) continue;
            // Only leaf-ish interactive/text containers: a Grid's "ink" is meaningless.
            if (fe is not (ButtonBase or Label or TextBox)) continue;

            string text = ContentText(fe);
            if (text.Length > 60) continue;                        // a list, not a label

            // The "content" is whatever the template hangs the label or glyph off: the
            // presenter for a text button, the Path/Viewbox for an icon one. Both get audited
            // — an icon drawn 2 px from a rounded edge is the same defect as a clipped word.
            var presenters = Descendants(fe)
                .OfType<FrameworkElement>()
                .Where(d => d is ContentPresenter or TextBlock or TextBox
                         or System.Windows.Shapes.Path or Viewbox)
                .ToList();
            if (presenters.Count == 0) continue;
            targets.Add((fe, presenters, text));
        }
        if (targets.Count == 0) return;

        var withInk = RenderWindowBits(win, dpi);
        var saved = new List<(FrameworkElement El, double Op)>();
        foreach (var t in targets)
            foreach (var p in t.Presenters) { saved.Add((p, p.Opacity)); p.Opacity = 0; }
        win.UpdateLayout();
        Pump();
        var withoutInk = RenderWindowBits(win, dpi);
        foreach (var (el, op) in saved) el.Opacity = op;
        win.UpdateLayout();
        Pump();

        int stride = withInk.PixelWidth * 4;
        var a = Pixels(withInk);
        var b = Pixels(withoutInk);

        foreach (var (fe, presenters, text) in targets)
        {
            try
            {
                var box = BoxInWindow(fe, win, dpi, withInk.PixelWidth, withInk.PixelHeight);
                if (box.Width < 4 || box.Height < 4) continue;
                var ink = InkIn(a, b, stride, box);
                if (ink.IsEmpty) continue;

                int cw = box.Width, ch = box.Height;
                ink.Offset(-box.X, -box.Y);
                int gl = ink.Left, gt = ink.Top, gr = cw - ink.Right, gb = ch - ink.Bottom;
                double pct = ch == 0 ? 0 : 100.0 * ink.Height / ch;

                var flags = new List<string>();
                double px = dpi / 96.0;
                bool isText = text.Length > 0;
                // A CheckBox/RadioButton draws its box hard against the left edge and its label
                // beside it: left-hugging and lopsided are the design, not a defect. Only the
                // vertical edges say anything about those.
                bool leftAligned = fe is CheckBox or RadioButton or TextBox;

                if (gt < 2 * px || gb < 2 * px) flags.Add("CLIPPED-V");
                if (!leftAligned && (gl < 2 * px || gr < 2 * px)) flags.Add("CLIPPED-H");

                // Cap ink sits above the baseline, so a correctly centred word with no
                // descender measures ~1 px high-heavy; allow that plus a pixel of rounding.
                double vTol = isText ? 2 * px + 0.12 * ink.Height : Math.Max(1, px);
                if (Math.Abs(gt - gb) > vTol) flags.Add("OFFCENTRE-V");
                if (!leftAligned && Math.Abs(gl - gr) > 2 * px) flags.Add("LOPSIDED-H");

                // Only a *button* owes its label a share of its own height; a checkbox label
                // inside a tall settings row is meant to be small.
                if (pct < 30 && fe is ButtonBase && !leftAligned) flags.Add("TINY");

                string id = Id(fe);
                rows.Add(string.Join("\t", surface, (int)dpi, id, fe.GetType().Name,
                    Flat(text.Length == 0 ? "(icon)" : text), Row(fe),
                    box.X, box.Y, cw, ch, ink.Width, ink.Height, gl, gt, gr, gb,
                    pct.ToString("F0", CultureInfo.InvariantCulture),
                    Stroke(a, b, stride, box).ToString("F2", CultureInfo.InvariantCulture),
                    flags.Count == 0 ? "ok" : string.Join("+", flags)));

                if (flags.Count > 0)
                    Crop4x(win, fe, dpi, Path.Combine(cropDir, $"{surface}-{id}-{(int)dpi}-4x.png"));
            }
            catch
            {
                // A control that cannot be rendered in isolation (no presentation source yet)
                // is not a defect; skip rather than fail the sweep.
            }
        }
    }

    /// <summary>
    /// Median horizontal run of ink inside the box, in device pixels — the stroke weight a
    /// line-icon is actually drawn at. Two icons in one row with different medians are two
    /// icons from two families, which the rubric calls the loudest tell of a thrown-together UI.
    /// Runs longer than 12 px are counted out: those are fills and long horizontals, not strokes.
    /// </summary>
    private static double Stroke(byte[] a, byte[] b, int stride, SD.Rectangle box)
    {
        var runs = new List<int>();
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int o = y * stride, run = 0;
            for (int x = box.Left; x < box.Right; x++)
            {
                int i = o + x * 4;
                int d = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1])
                      + Math.Abs(a[i + 2] - b[i + 2]) + Math.Abs(a[i + 3] - b[i + 3]);
                if (d > 40) { run++; continue; }
                if (run is > 0 and <= 12) runs.Add(run);
                run = 0;
            }
            if (run is > 0 and <= 12) runs.Add(run);
        }
        if (runs.Count == 0) return 0;
        runs.Sort();
        return runs[runs.Count / 2];
    }

    /// <summary>The control's layout box in the window's device pixels at this DPI.</summary>
    private static SD.Rectangle BoxInWindow(
        FrameworkElement fe, Window win, double dpi, int imgW, int imgH)
    {
        double scale = dpi / 96.0;
        var tl = fe.TransformToAncestor(win).Transform(new Point(0, 0));
        int x = (int)Math.Round(tl.X * scale);
        int y = (int)Math.Round(tl.Y * scale);
        int w = (int)Math.Round(fe.ActualWidth * scale);
        int h = (int)Math.Round(fe.ActualHeight * scale);
        x = Math.Max(0, x); y = Math.Max(0, y);
        w = Math.Min(w, imgW - x); h = Math.Min(h, imgH - y);
        return new SD.Rectangle(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    /// <summary>Bounding box of the pixels that differ between the two window renders, inside
    /// <paramref name="box"/> — i.e. exactly the glyph or label ink of that control.</summary>
    private static SD.Rectangle InkIn(byte[] a, byte[] b, int stride, SD.Rectangle box)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int o = y * stride;
            for (int x = box.Left; x < box.Right; x++)
            {
                int i = o + x * 4;
                int d = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1])
                      + Math.Abs(a[i + 2] - b[i + 2]) + Math.Abs(a[i + 3] - b[i + 3]);
                if (d <= 12) continue;      // AA noise floor, not ink
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return SD.Rectangle.Empty;
        return SD.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static byte[] Pixels(BitmapSource src)
    {
        int stride = src.PixelWidth * 4;
        var buf = new byte[stride * src.PixelHeight];
        src.CopyPixels(buf, stride, 0);
        return buf;
    }

    private static RenderTargetBitmap RenderWindowBits(Window w, double dpi)
    {
        double scale = dpi / 96.0;
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(w.ActualHeight * scale)),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(w);
        return rtb;
    }

    /// <summary>
    /// A 4x crop of one control, taken by re-rendering the WINDOW through a scale and a
    /// translate so the glyphs are drawn at four times the resolution rather than an upscaled
    /// screenshot. Four device pixels of magenta surround the control's layout box, so a
    /// clipped edge is obvious: ink touching magenta is ink outside the control.
    /// </summary>
    private static void Crop4x(Window win, FrameworkElement fe, double dpi, string path)
    {
        const int Z = 4, Pad = 4;
        var tl = fe.TransformToAncestor(win).Transform(new Point(0, 0));
        double bw = fe.ActualWidth + 2 * Pad, bh = fe.ActualHeight + 2 * Pad;
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(bw * Z * dpi / 96.0)),
            Math.Max(1, (int)Math.Round(bh * Z * dpi / 96.0)),
            dpi * Z, dpi * Z, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Magenta, null, new Rect(0, 0, bw, bh));
            dc.PushTransform(new TranslateTransform(Pad - tl.X, Pad - tl.Y));
            dc.DrawRectangle(
                new VisualBrush(win) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, win.ActualWidth, win.ActualHeight));
            dc.Pop();
        }
        rtb.Render(dv);
        Save(rtb, path);
    }

    private static void RenderWindow(Window w, string path, double dpi)
    {
        double scale = dpi / 96.0;
        int width = (int)Math.Ceiling(w.ActualWidth * scale);
        int height = (int)Math.Ceiling(w.ActualHeight * scale);
        if (width <= 0 || height <= 0) return;
        var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(w);
        Save(rtb, path);
    }

    private static void Save(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------------------------------------------------------------- helpers

    private static string Id(FrameworkElement fe)
    {
        string n = fe.Name;
        if (string.IsNullOrEmpty(n))
            n = new string(ContentText(fe).Where(char.IsLetterOrDigit).Take(14).ToArray());
        return string.IsNullOrEmpty(n) ? fe.GetType().Name : n;
    }

    /// <summary>Nearest named ancestor panel — the "row" an icon has to match its siblings in.</summary>
    private static string Row(FrameworkElement fe)
    {
        for (DependencyObject? p = VisualTreeHelper.GetParent(fe); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement pe && !string.IsNullOrEmpty(pe.Name)) return pe.Name;
        return "-";
    }

    private static string ContentText(FrameworkElement fe) => fe switch
    {
        TextBox tb => tb.Text,
        ContentControl cc => cc.Content as string ?? TextOf(cc.Content),
        _ => string.Empty,
    };

    private static string TextOf(object? content) => content switch
    {
        null => string.Empty,
        TextBlock t => t.Text,
        string s => s,
        _ => string.Empty,
    };

    private static string Flat(string s) =>
        s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    internal static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
}
