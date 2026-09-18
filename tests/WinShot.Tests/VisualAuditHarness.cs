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
                // A real WPF UI thread runs inside a dispatcher loop, so an `await` in a
                // handler resumes back on it. This thread drives the windows directly, where
                // SynchronizationContext.Current is null and every continuation lands on the
                // thread pool — the About tab's release-notes fetch then touched its status
                // text from there and took the whole test host down.
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
                        // Long enough for a tab's reveal animation to finish: at eight rounds
                        // the About cards were still sliding in and the first DPI pass measured
                        // a half-drawn disclosure toggle as a clipped one.
                        for (int i = 0; i < 30; i++) { Pump(); Thread.Sleep(20); }
                        win.UpdateLayout();
                        Pump();

                        foreach (double dpi in Dpis)
                        {
                            RenderWindow(win, Path.Combine(outDir, $"{name}-{(int)dpi}.png"), dpi);
                            AuditControls(win, name, dpi, cropDir, rows);
                        }

                        AuditFloatingLayers(win, name, outDir, cropDir, rows);
                        if (name == "state-sheet") WriteStateTable(win, outDir);
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
        // Every contextual sub-row in the editor starts Collapsed and is revealed by the tool
        // that owns it, so a single resting editor render measured none of them. One surface per
        // tool puts the crop bar, the arrowhead row, the redaction row, the counter picker, the
        // rich-text strip and the magnifier row in front of the instrument.
        foreach (string tool in ContextualTools)
        {
            string t = tool;
            yield return ($"editor-{t.ToLowerInvariant()}",
                () => new EditorWindow(Shot(1200, 760), settings, history), w => PickTool(w, t));
        }
        yield return ("history", () => new HistoryWindow(history, settings), null);
        yield return ("settings-general", () => new SettingsWindow(settings), null);
        yield return ("settings-shortcuts", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 1));
        yield return ("settings-quick-access", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 2));
        yield return ("settings-recording", () => new SettingsWindow(settings),
            w => { Select(w, "SectionList", 3); Pump(); Select(w, "RecordingSubList", 0); });
        yield return ("settings-screenshots", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 4));
        yield return ("settings-annotate", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 5));
        yield return ("settings-advanced", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 6));
        yield return ("settings-about", () => new SettingsWindow(settings),
            w => Select(w, "SectionList", 7));
        yield return ("resize-dialog", () => new ResizeDialog(1200, 760), null);
        // Every style in the theme, six states each. See VisualAuditStateSheet.
        yield return ("state-sheet", VisualAuditStateSheet.Build, null);
        // The dialog card, built rather than shown: ShowDialog blocks, and Build hands back
        // exactly what it would have shown.
        yield return ("dialog-ok", () => Dialog("Couldn't save", ShortBody, ("OK", true)), null);
        yield return ("dialog-confirm",
            () => Dialog("Delete 12 screenshots?", ShortBody, ("Cancel", false), ("Delete", true)), null);
        yield return ("dialog-destructive",
            () => Dialog("Delete 12 screenshots?", ShortBody, ("Keep them", false), ("Delete", null)), null);
        yield return ("dialog-three",
            () => Dialog("Unsaved changes", ShortBody,
                ("Discard", false), ("Cancel", false), ("Save", true)), null);
        yield return ("dialog-long", () => Dialog("Couldn't reach the update server", LongBody, ("OK", true)), null);
        yield return ("background-composer",
            () => new WinShot.Editor.Background.BackgroundComposerWindow(Shot(800, 500), settings, history), null);
        // Both editors open on an empty temp file: the chrome is what is being audited and no
        // frame is ever decoded, so nothing of the user's is rendered.
        yield return ("video-editor",
            () => new WinShot.Recording.VideoEditorWindow(EmptyMedia("mp4"), settings, history), null);
        yield return ("gif-editor",
            () => new WinShot.Recording.GifEditorWindow(EmptyMedia("gif"), settings, history), null);
        yield return ("recording-recovery", () => new WinShot.Recording.RecordingRecoveryWindow(
            new[]
            {
                new WinShot.Recording.RecoverableRecordingTempFile(
                    @"C:\Temp\WinShot\recording-1.mp4", ".mp4", 48_234_496, DateTime.UtcNow.AddHours(-2)),
                new WinShot.Recording.RecoverableRecordingTempFile(
                    @"C:\Temp\WinShot\recording-2.gif", ".gif", 3_145_728, DateTime.UtcNow.AddDays(-1)),
            }), null);
    }

    private const string ShortBody = "This cannot be undone.";

    /// <summary>
    /// Long enough to wrap to four lines in a 420 DIP card — the case where a dialog that
    /// sizes to its content either grows correctly or clips its last line.
    /// </summary>
    private const string LongBody =
        "WinShot could not reach the update server, so it does not know whether a newer build "
        + "exists. Your screenshots and recordings are unaffected. Check your connection and "
        + "try again, or carry on — WinShot will look again the next time it starts.";

    /// <summary>
    /// A dialog card as the app builds it. Buttons run left to right with the primary LAST;
    /// a null "primary" flag means the destructive fill.
    /// </summary>
    private static Window Dialog(string title, string body, params (string Label, bool? Primary)[] buttons)
    {
        var (dialog, _, footer) = ThemedMessageDialog.Build(null, title, body);
        foreach (var (label, primary) in buttons)
            ThemedMessageDialog.AddButton(
                footer, label, primary == true, () => { },
                primary is null ? "PillButtonDestructive" : null);
        return dialog;
    }

    private static string EmptyMedia(string ext)
    {
        string p = Path.Combine(Path.GetTempPath(), $"winshot-visual-audit-{Guid.NewGuid():N}.{ext}");
        File.WriteAllBytes(p, Array.Empty<byte>());
        return p;
    }

    /// <summary>
    /// The tools whose sub-row panel is a surface of its own. Select* is the resting editor and
    /// is already covered by the plain "editor" surface.
    /// </summary>
    private static readonly string[] ContextualTools =
    {
        "Rectangle", "Arrow", "Line", "Text", "Callout", "Step",
        "Redact", "Magnifier", "Crop", "Freehand", "Highlighter", "Polygon",
    };

    private static void PickTool(Window w, string tool)
    {
        if (w.FindName(tool + "ToolBtn") is System.Windows.Controls.Primitives.ToggleButton b)
            b.IsChecked = true;
    }

    /// <summary>
    /// Every popover, dropdown and context menu in the app lives in a Popup, whose content is a
    /// separate top-level window: <c>RenderTargetBitmap.Render(ownerWindow)</c> never reaches it,
    /// which is why the first sweep measured none of them. The handle is
    /// <see cref="PresentationSource"/> — an open popup has one of its own, whose RootVisual is
    /// the PopupRoot holding the card, its shadow margin and all. So open one layer at a time,
    /// diff the PopupRoot instead of the window, and the whole existing instrument applies.
    /// </summary>
    private static void AuditFloatingLayers(
        Window win, string surface, string outDir, string cropDir, List<string> rows)
    {
        foreach (var (layer, open, close, child) in FloatingLayers(surface, win))
        {
            string name = $"{surface}.{layer}";
            try
            {
                open();
                for (int i = 0; i < 20; i++) { Pump(); Thread.Sleep(20); }
                // WPF POOLS a popup's HwndSource: close one popover and the next one to open
                // reuses the same window, so "a presentation source that was not there before"
                // found only the first layer per surface and called the other ten never opened.
                // Ask the popup's own child which source it is hanging in instead.
                var c = child();
                var root = c is null ? null
                    : PresentationSource.FromVisual(c)?.RootVisual as FrameworkElement;
                if (root is null || root.ActualWidth < 4)
                {
                    rows.Add($"{name}\t-\t-\t-\tNEVER OPENED\t-\t\t\t\t\t\t\t\t\tERROR");
                    continue;
                }
                root.UpdateLayout();
                Pump();
                foreach (double dpi in Dpis)
                {
                    RenderWindow(root, Path.Combine(outDir, $"{name}-{(int)dpi}.png"), dpi);
                    AuditControls(root, name, dpi, cropDir, rows);
                }
            }
            catch (Exception ex)
            {
                rows.Add($"{name}\t-\t-\t-\tLAYER FAILED: {ex.GetType().Name}: {Flat(ex.Message)}\t-\t\t\t\t\t\t\t\t\tERROR");
            }
            finally
            {
                try { close(); } catch { }
                Pump();
            }
        }
    }

    private static IEnumerable<(string Name, Action Open, Action Close, Func<FrameworkElement?> Child)>
        FloatingLayers(string surface, Window win)
    {
        // A Popup reached by name: IsOpen is the whole trigger, and it does not care whether the
        // button that normally opens it is on screen.
        // Open a popover through the BUTTON that opens it, not by setting IsOpen: the colour
        // picker builds its sixteen preset swatches in the click handler, so a popup forced open
        // from the side rendered an empty card and the grid went unaudited for a whole round.
        (string, Action, Action, Func<FrameworkElement?>) Pop(
            string label, string popupName, string trigger, Action? prep = null)
        {
            var p = win.FindName(popupName) as System.Windows.Controls.Primitives.Popup;
            return (label,
                () =>
                {
                    if (p is null) return;
                    switch (win.FindName(trigger))
                    {
                        case System.Windows.Controls.Primitives.ToggleButton t: t.IsChecked = true; break;
                        case ButtonBase b: b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                    }
                    Pump();
                    prep?.Invoke();
                    Pump();
                    p.IsOpen = true;
                },
                () =>
                {
                    if (win.FindName(trigger) is System.Windows.Controls.Primitives.ToggleButton t)
                        t.IsChecked = false;
                    if (p is not null) p.IsOpen = false;
                },
                () => p?.Child as FrameworkElement);
        }

        (string, Action, Action, Func<FrameworkElement?>) Drop(string label, string comboName)
        {
            var c = win.FindName(comboName) as ComboBox;
            return (label,
                () => { if (c is not null) c.IsDropDownOpen = true; },
                () => { if (c is not null) c.IsDropDownOpen = false; },
                () => (c?.Template?.FindName("PART_Popup", c)
                        as System.Windows.Controls.Primitives.Popup)?.Child as FrameworkElement);
        }

        (string, Action, Action, Func<FrameworkElement?>) Menu(
            string label, ContextMenu? menu, UIElement? target)
        {
            return (label,
                () =>
                {
                    if (menu is null) return;
                    menu.PlacementTarget = target;
                    menu.IsOpen = true;
                },
                () => { if (menu is not null) menu.IsOpen = false; },
                () => menu);
        }

        if (surface == "editor" || surface.StartsWith("editor-", StringComparison.Ordinal))
        {
            yield return Pop("colour-grid", "ColorPickerPopup", "ColorWellBtn");
            yield return Pop("colour-spectrum", "ColorPickerPopup", "ColorWellBtn",
                () => Check(win, "PickerSpectrumModeBtn"));
            yield return Pop("size-presets", "SizePresetsPopup", "SizePresetsBtn");
            yield return Pop("font-colour", "FontColorPopup", "FontColorBtn");
            yield return Pop("align-grid", "AlignPopup", "AlignMenuBtn");
            yield return Pop("overflow", "FormatOverflowPopup", "FormatOverflowBtn");
            yield return Pop("eraser-mode", "EraserModePopup", "EraserCaretBtn");
            yield return Pop("fit-menu", "FitMenuPopup", "FitMenuBtn");
            foreach (string combo in new[]
                     {
                         "ArrowStyleBox", "LineStyleBox", "FontFamilyBox", "FontSizeBox",
                         "AlignBox", "VerticalAlignBox", "CropRatioBox", "CounterSeriesBox",
                         "StepModeBox", "RedactStyleBox",
                     })
                yield return Drop(combo.Replace("Box", "").ToLowerInvariant(), combo);
            yield return Menu("annotation-menu", AnnotationMenu(win), win);
            yield return Menu("counter-row-menu", CounterRowMenu(win), win);
        }
        else if (surface == "history")
        {
            yield return Menu("card-menu", FirstContextMenu(win), win);
        }
        else if (surface.StartsWith("settings-", StringComparison.Ordinal))
        {
            foreach (var combo in Descendants(win).OfType<ComboBox>()
                         .Where(c => c.IsVisible && !string.IsNullOrEmpty(c.Name)).Take(3).ToList())
                yield return Drop(combo.Name.ToLowerInvariant(), combo.Name);
        }
    }

    /// <summary>
    /// The right-click menu on a counter-series row, which only exists once a row does. The
    /// editor builds one per series, so ask it for a row's menu the way the list does.
    /// </summary>
    private static ContextMenu? CounterRowMenu(Window win)
    {
        var box = win.FindName("CounterSeriesBox") as ComboBox;
        return box?.Items.OfType<ComboBoxItem>().Select(i => i.ContextMenu)
            .FirstOrDefault(m => m is not null);
    }

    private static void Check(Window w, string name)
    {
        if (w.FindName(name) is System.Windows.Controls.Primitives.ToggleButton t) t.IsChecked = true;
    }

    /// <summary>
    /// The editor's annotation right-click menu is built on demand in code, so the only way to
    /// see it is to ask the window for it the way the right-click path does.
    /// </summary>
    private static ContextMenu? AnnotationMenu(Window win)
    {
        var m = win.GetType().GetMethod("BuildAnnotationMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return m?.Invoke(win, null) as ContextMenu;
    }

    /// <summary>History's card menu hangs off the first card; with an empty library it hangs off
    /// the list, so take whichever element in the window carries one.</summary>
    private static ContextMenu? FirstContextMenu(Window win) =>
        Descendants(win).OfType<FrameworkElement>()
            .Select(f => f.ContextMenu).FirstOrDefault(m => m is not null);

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
        FrameworkElement win, string surface, double dpi, string cropDir, List<string> rows)
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
            // IsVisible is not the same as visible. A History card's action overlay sits at
            // Opacity 0 until the pointer is on the card; measuring it diffed the SCREENSHOT
            // behind it and reported a red annotation as a clipped Delete label.
            if (Faded(fe, win)) continue;
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
        var savedFg = new List<(TextBox Box, Brush Fg)>();
        foreach (var t in targets)
        {
            // A TextBox draws its own text through a template content host, not through any
            // element whose Opacity we can reach, so hiding presenters left the text on screen
            // and the diff picked up a stray hairline instead — the resize dialog's fields all
            // measured 3 px of "ink". Its Foreground is the handle that works.
            if (t.Fe is TextBox box)
            {
                savedFg.Add((box, box.Foreground));
                box.Foreground = Brushes.Transparent;
                continue;
            }
            foreach (var p in t.Presenters) { saved.Add((p, p.Opacity)); p.Opacity = 0; }
        }
        win.UpdateLayout();
        Pump();
        var withoutInk = RenderWindowBits(win, dpi);
        foreach (var (el, op) in saved) el.Opacity = op;
        foreach (var (box, fg) in savedFg) box.Foreground = fg;
        win.UpdateLayout();
        Pump();

        int stride = withInk.PixelWidth * 4;
        var a = Pixels(withInk);
        var b = Pixels(withoutInk);

        // Pass 1: measure. Pass 2: flag, because "this icon is out of step with its row" can
        // only be answered once the rest of the row has been measured.
        var seen = new List<Measured>();
        foreach (var (fe, presenters, text) in targets)
        {
            try
            {
                var box = BoxInWindow(fe, win, dpi, withInk.PixelWidth, withInk.PixelHeight);
                if (box.Width < 4 || box.Height < 4) continue;
                var ink = InkIn(a, b, stride, box);
                if (ink.IsEmpty) continue;
                ink.Offset(-box.X, -box.Y);
                seen.Add(new Measured(fe, Id(fe), text, Row(fe), box, ink,
                    Stroke(a, b, stride, box)));
            }
            catch
            {
                // A control with no presentation source yet is not a defect; skip it.
            }
        }

        var iconRows = seen
            .Where(m => m.Text.Length == 0 && m.Fe is ButtonBase)
            .GroupBy(m => m.RowName)
            .ToList();
        var rowInkHeights = iconRows.ToDictionary(
            g => g.Key, g => Median(g.Select(m => (double)m.Ink.Height).ToList()));
        var rowCounts = iconRows.ToDictionary(g => g.Key, g => g.Count());

        double px = dpi / 96.0;
        foreach (var m in seen)
        {
            int cw = m.Box.Width, ch = m.Box.Height;
            int gl = m.Ink.Left, gt = m.Ink.Top, gr = cw - m.Ink.Right, gb = ch - m.Ink.Bottom;
            double pct = ch == 0 ? 0 : 100.0 * m.Ink.Height / ch;
            bool isText = m.Text.Length > 0;
            // A CheckBox or RadioButton IS its box: the box defines the control's height and
            // hugs its left edge, so clipping and lopsidedness mean nothing there. Flagging
            // them buried the real findings under twenty rows of noise.
            bool boxIsTheControl = m.Fe is CheckBox or RadioButton
                || (m.Fe is ToggleButton && m.Fe.TemplatedParent is ComboBox);

            var flags = new List<string>();
            if (!boxIsTheControl)
            {
                if (gt < 2 * px || gb < 2 * px) flags.Add("CLIPPED-V");
                if (m.Fe is not TextBox && (gl < 2 * px || gr < 2 * px)) flags.Add("CLIPPED-H");
                if (m.Fe is not TextBox && Math.Abs(gl - gr) > 2 * px) flags.Add("LOPSIDED-H");
            }

            // Cap ink sits above the baseline, so a correctly centred word with no descender
            // measures ~1 px high-heavy; allow that plus a pixel of rounding.
            // A cap stops 0.70em above the baseline and a descender falls 0.29em below it, so
            // a perfectly centred word measures top-heavy in proportion to its own ink height.
            // At 0.12 this flagged Copy, Drag & Drop and Scrolling, all of which sit correctly.
            double vTol = isText ? 2 * px + 0.25 * m.Ink.Height : Math.Max(1, px);
            if (Math.Abs(gt - gb) > vTol) flags.Add("OFFCENTRE-V");

            // A 13 px label in a 30 px pill measures 30 % — that is the normal, correct value,
            // so the bar sits below it. Under a quarter of the button is a label that reads as
            // a small word lost in a big control.
            if (isText && pct < 26 && m.Fe is ButtonBase && !boxIsTheControl) flags.Add("TINY");

            // An icon is judged against its own row, not against a constant: what makes a row
            // look thrown together is one glyph drawn a different size from its neighbours.
            // Four icons minimum: the zoom rail holds three, one of which is a minus sign, so
            // its median said every other glyph in the rail was the odd one out.
            if (!isText && m.Fe is ButtonBase
                && rowInkHeights.TryGetValue(m.RowName, out double med) && med > 0
                && rowCounts[m.RowName] >= 4
                && Math.Abs(m.Ink.Height - med) / med > 0.25)
                flags.Add($"ODD-SIZE({m.Ink.Height}v{med:F0})");

            rows.Add(string.Join("\t", surface, (int)dpi, m.Id, m.Fe.GetType().Name,
                Flat(isText ? m.Text : "(icon)"), m.RowName,
                m.Box.X, m.Box.Y, cw, ch, m.Ink.Width, m.Ink.Height, gl, gt, gr, gb,
                pct.ToString("F0", CultureInfo.InvariantCulture),
                m.Stroke.ToString("F2", CultureInfo.InvariantCulture),
                flags.Count == 0 ? "ok" : string.Join("+", flags)));

            if (flags.Count > 0)
                Crop4x(win, m.Fe, dpi, Path.Combine(cropDir, $"{surface}-{m.Id}-{(int)dpi}-4x.png"));
        }
    }

    /// <summary>
    /// The one thing about a state that cannot be judged by looking at six cells side by side:
    /// whether PRESSED is darker than resting. The accent ladder in the palette only ever goes
    /// down — Accent, AccentDeep, AccentPressed — and a template that lightens on press is
    /// reading it upside down. Mean luminance inside each cell, one row per style and state.
    /// </summary>
    private static void WriteStateTable(Window win, string outDir)
    {
        var bits = RenderWindowBits(win, 144);
        var px = Pixels(bits);
        int stride = bits.PixelWidth * 4;
        var lines = new List<string> { "style	state	mean_r	mean_g	mean_b	luminance" };

        foreach (var fe in Descendants(win).OfType<FrameworkElement>())
        {
            if (fe is not Control || string.IsNullOrEmpty(fe.Name) || !fe.Name.Contains('_')) continue;
            if (!fe.IsVisible || fe.ActualWidth < 6 || fe.ActualHeight < 6) continue;
            var box = BoxInWindow(fe, win, 144, bits.PixelWidth, bits.PixelHeight);
            if (box.Width < 4 || box.Height < 4) continue;

            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    int i = y * stride + x * 4;
                    b += px[i]; g += px[i + 1]; r += px[i + 2];
                    n++;
                }
            if (n == 0) continue;
            r /= n; g /= n; b /= n;
            int cut = fe.Name.LastIndexOf('_');
            lines.Add(string.Join("	", fe.Name[..cut], fe.Name[(cut + 1)..],
                r.ToString("F1", CultureInfo.InvariantCulture),
                g.ToString("F1", CultureInfo.InvariantCulture),
                b.ToString("F1", CultureInfo.InvariantCulture),
                Luminance(r, g, b).ToString("F4", CultureInfo.InvariantCulture)));
        }
        File.WriteAllLines(Path.Combine(outDir, "states.tsv"), lines);
    }

    private static double Luminance(double r, double g, double b)
    {
        static double Ch(double v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }

    /// <summary>Is this control, or anything it hangs under, faded out to nothing?</summary>
    private static bool Faded(DependencyObject? fe, DependencyObject root)
    {
        for (var p = fe; p is not null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p))
            if (p is UIElement u && u.Opacity < 0.05) return true;
        return false;
    }

    private sealed record Measured(
        FrameworkElement Fe, string Id, string Text, string RowName,
        SD.Rectangle Box, SD.Rectangle Ink, double Stroke);

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[v.Count / 2];
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
        FrameworkElement fe, FrameworkElement win, double dpi, int imgW, int imgH)
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

    private static RenderTargetBitmap RenderWindowBits(FrameworkElement w, double dpi)
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
    private static void Crop4x(FrameworkElement win, FrameworkElement fe, double dpi, string path)
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
            // The window's LAYOUT box, mapped 1:1. Stretch.None aligns a visual's CONTENT
            // bounds, which for a window with a drop shadow or an open popup start outside the
            // layout box and slid every crop by the overhang.
            var page = new Rect(0, 0, win.ActualWidth, win.ActualHeight);
            dc.DrawRectangle(
                new VisualBrush(win)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = page,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = page,
                    TileMode = TileMode.None,
                },
                null, page);
            dc.Pop();
        }
        rtb.Render(dv);
        Save(rtb, path);
    }

    private static void RenderWindow(FrameworkElement w, string path, double dpi)
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

    /// <summary>
    /// The words a control shows, if any. A button holding an icon AND a label — Drag &amp; Drop,
    /// the History sort button — is a text button: judging its ink with an icon's one-pixel
    /// centring tolerance flagged three labels that were sitting exactly where they should.
    /// </summary>
    private static string ContentText(FrameworkElement fe)
    {
        if (fe is TextBox tb) return tb.Text;
        if (fe is ContentControl cc && cc.Content is string s) return s;
        return string.Concat(Descendants(fe).OfType<TextBlock>().Select(t => t.Text));
    }

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
