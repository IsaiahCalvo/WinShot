using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
/// The "does it LOOK good" probe. Similarity-to-Survey numbers cannot answer that question:
/// a glyph can match its source path data exactly and still render as a grey smear because
/// the host scaled it to a size where its stroke lands on 0.875 device pixels.
///
/// Two outputs, per DPI (96 / 120 / 144 / 192 — Isaiah runs 150 %, i.e. 144):
///   toolbar-&lt;dpi&gt;.png  — the real editor's top bar, each group's sub-row, the zoom rail
///                          and the bottom bar, cropped out of a live EditorWindow.
///   icons-&lt;dpi&gt;.png    — every SurveyIcon.* resource rendered in isolation at each host
///                          size the shipped templates actually use (14 / 16 / 18 / 20 DIP),
///                          1:1, plus a 4x nearest-neighbour blow-up of the 18 so half-pixel
///                          stroke placement is visible to a human eye.
///
/// Gated behind WINSHOT_RENDER_TOOLBARICONS=1 because it creates a WPF Application.
/// Output dir: WINSHOT_RENDER_OUT.
/// </summary>
public class ToolbarIconProbe
{
    private const double Magnify = 4;

    [Fact]
    public void RenderToolbarAndIconContactSheets()
    {
        if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_TOOLBARICONS") != "1")
            return;

        string outDir = Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUT")
            ?? Path.Combine(Path.GetTempPath(), "winshot-toolbar-icons");
        Directory.CreateDirectory(outDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is { } inherited && !inherited.CheckAccess())
                    throw new InvalidOperationException(
                        "Another test owns this AppDomain's Application. Run this probe alone.");

                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                foreach (int dpi in new[] { 96, 120, 144, 192 })
                    RenderIconSheet(outDir, dpi);

                foreach (int dpi in new[] { 96, 144, 192 })
                    RenderSurveyOrderSheet(outDir, dpi);

                if (Environment.GetEnvironmentVariable("WINSHOT_ICON_EXPERIMENT") == "1")
                    foreach (int dpi in new[] { 96, 144 })
                        RenderExperiment(outDir, dpi);

                RenderToolbars(outDir);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    // ------------------------------------------------------------------ icons

    /// <summary>Every SurveyIcon.* key in the theme, in dictionary order.</summary>
    private static List<string> IconKeys()
    {
        var theme = new ResourceDictionary { Source = new Uri(ThemeResources.ThemePath, UriKind.Absolute) };
        var keys = new List<string>();
        Walk(theme, keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;

        static void Walk(ResourceDictionary d, List<string> into)
        {
            foreach (DictionaryEntry e in d)
                if (e.Key is string s && s.StartsWith("SurveyIcon.", StringComparison.Ordinal) && !into.Contains(s))
                    into.Add(s);
            foreach (var m in d.MergedDictionaries) Walk(m, into);
        }
    }

    private static readonly double[] HostSizes = { 14, 16, 18, 20 };

    private static void RenderIconSheet(string outDir, int dpi)
    {
        var keys = IconKeys();
        var sheet = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // name
        foreach (var _ in HostSizes)
            sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18 * Magnify + 16) });

        // header
        sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        Put(sheet, Label($"{dpi} dpi  ({dpi / 96.0:0.##}x)"), 0, 0);
        for (int c = 0; c < HostSizes.Length; c++)
            Put(sheet, Label($"{HostSizes[c]:0}"), 0, c + 1);
        Put(sheet, Label($"18 @ {Magnify:0}x"), 0, HostSizes.Length + 1);

        int row = 1;
        foreach (string key in keys)
        {
            sheet.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18 * Magnify + 10) });
            Put(sheet, Label(key.Substring("SurveyIcon.".Length)), row, 0);

            for (int c = 0; c < HostSizes.Length; c++)
                Put(sheet, IconAt(key, HostSizes[c], 1, dpi), row, c + 1);

            Put(sheet, IconAt(key, 18, Magnify, dpi), row, HostSizes.Length + 1);
            row++;
        }

        sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        sheet.Arrange(new Rect(sheet.DesiredSize));
        sheet.UpdateLayout();
        Save(sheet, Path.Combine(outDir, $"icons-{dpi}.png"), dpi);

        if (dpi == 96) WriteInkTable(outDir, keys);
    }

    /// <summary>
    /// Survey's own Shapes glyph carries the house law in its header comment: a toolbar glyph
    /// should paint about the same INK as its neighbours, and the owner's pre-polish reject was
    /// diagnosed by exactly this number ("109.7 px2 vs Draw 48.8 / Text 54.2, measured at 1x").
    /// So measure it: total coverage (sum of alpha) and the ink envelope (the fraction of the
    /// box the artwork actually occupies) for every glyph at the size its host really uses.
    /// A glyph twice its neighbours' ink is the one a human calls "too heavy" — and a glyph at
    /// half is the one they call "washed out" — without being able to say why.
    /// </summary>
    private static void WriteInkTable(string outDir, List<string> keys)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("glyph                 size   ink(px2)  envelope  meanEdgeAlpha");
        foreach (string key in keys)
        {
            foreach (double size in new double[] { 18, 20 })
            {
                var host = new Viewbox
                {
                    Width = size,
                    Height = size,
                    Child = new ContentControl
                    {
                        Content = (FrameworkElement)Application.Current.TryFindResource(key)!,
                        Foreground = Brushes.White,
                    },
                };
                var wrap = new Grid { Width = size, Height = size };
                wrap.Children.Add(host);
                wrap.Measure(new Size(size, size));
                wrap.Arrange(new Rect(0, 0, size, size));
                wrap.UpdateLayout();

                // 8x supersample, so a 1.1px stroke is measured, not rounded away.
                const int SS = 8;
                int px = (int)(size * SS);
                var rtb = new RenderTargetBitmap(px, px, 96 * SS, 96 * SS, PixelFormats.Pbgra32);
                rtb.Render(wrap);
                var buf = new byte[px * px * 4];
                rtb.CopyPixels(buf, px * 4, 0);

                double ink = 0;
                int minX = px, maxX = -1, minY = px, maxY = -1;
                for (int y = 0; y < px; y++)
                    for (int x = 0; x < px; x++)
                    {
                        double a = buf[(y * px + x) * 4 + 3] / 255.0;
                        if (a <= 0.02) continue;
                        ink += a;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                ink /= SS * SS;
                double env = maxX < 0 ? 0 : Math.Max(maxX - minX + 1, maxY - minY + 1) / (double)px;

                // How solid is the ink? A glyph whose pixels never reach full alpha is the
                // one that reads as a grey smear rather than a line.
                double solid = 0; int lit = 0;
                for (int i = 3; i < buf.Length; i += 4)
                {
                    double a = buf[i] / 255.0;
                    if (a > 0.02) { solid += a; lit++; }
                }
                solid = lit == 0 ? 0 : solid / lit;

                sb.AppendLine($"{key.Substring(11),-20} {size,4:0}  {ink,9:0.0}  {env,7:0.0%}  {solid,12:0.000}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "ink.txt"), sb.ToString());
    }

    /// <summary>
    /// One icon inside the SAME nesting the shipped template gives it: a host Viewbox of
    /// `size` around the resource. `mag` blows the finished thing up with a ScaleTransform so
    /// the render samples it at mag x device resolution, which is what makes a half-pixel
    /// stroke visible instead of merely present.
    /// </summary>
    private static FrameworkElement IconAt(string key, double size, double mag, int dpi)
    {
        var res = (FrameworkElement)Application.Current.TryFindResource(key)!;
        var host = new Viewbox
        {
            Width = size,
            Height = size,
            Child = new ContentControl
            {
                Content = res,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            },
        };
        if (mag == 1) return Center(host);

        // Render at 1:1 into a bitmap, then magnify with NearestNeighbor: what the eye would
        // see under a loupe on the real pixels, not a re-render at a bigger size (which would
        // hide the very defect we are hunting).
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(host);
        grid.Measure(new Size(size, size));
        grid.Arrange(new Rect(0, 0, size, size));
        grid.UpdateLayout();
        // Sample at the SHEET's dpi: at 144 the glyph really is rasterised into 1.5x as many
        // device pixels, and blowing up a 96-dpi bitmap instead would show 96-dpi defects
        // under a 144-dpi label. The magnified cell is then drawn 1:1 in device pixels
        // (size*mag device px, i.e. size*mag*96/dpi DIP) so nothing resamples it a second time.
        int px = (int)Math.Round(size * dpi / 96.0);
        var rtb = new RenderTargetBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(grid);
        var img = new Image
        {
            Source = rtb,
            Width = px * mag * 96.0 / dpi,
            Height = px * mag * 96.0 / dpi,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return Center(img);
    }

    private static FrameworkElement Center(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
    };

    private static void Put(Grid g, FrameworkElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    /// <summary>
    /// The same glyphs, in the same order, at the same host sizes the RUNNING Survey uses —
    /// so this sheet can be laid beside the one Chrome drew from Survey's own sources and the
    /// rows line up. The sizes are not guesses: they are the computed width of each glyph's
    /// element in Survey @ c92c324a (inline svg width, or the mask span's box), read off the
    /// live app. Pan 20 and Select 22 are the two that WinShot had wrong at 16.
    /// </summary>
    private static readonly (string Key, double Size)[] SurveyOrder =
    {
        ("SurveyIcon.Undo", 14), ("SurveyIcon.Redo", 14),
        ("SurveyIcon.Pan", 20), ("SurveyIcon.Cursor", 22),
        ("SurveyIcon.DrawGroup", 18), ("SurveyIcon.Shapes", 18), ("SurveyIcon.TextGroup", 18),
        ("SurveyIcon.Pen", 20), ("SurveyIcon.Highlighter", 20), ("SurveyIcon.Eraser", 20),
        ("SurveyIcon.Rect", 20), ("SurveyIcon.Ellipse", 20), ("SurveyIcon.Line", 20),
        ("SurveyIcon.Arrow", 20), ("SurveyIcon.Counter", 20), ("SurveyIcon.Callout", 20),
    };

    private static void RenderSurveyOrderSheet(string outDir, int dpi)
    {
        const double rowH = 18 * Magnify + 10;
        var g = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18 * Magnify + 16) });

        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        Put(g, Label($"WINSHOT @ {dpi / 96.0:0.##}x   size    18@{Magnify:0}x"), 0, 0);

        for (int i = 0; i < SurveyOrder.Length; i++)
        {
            var (key, size) = SurveyOrder[i];
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH) });
            Put(g, Label($"{key.Substring(11)}  ({size:0})"), i + 1, 0);
            Put(g, IconAt(key, size, 1, dpi), i + 1, 1);
            Put(g, IconAt(key, size, Magnify, dpi), i + 1, 2);
        }

        g.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        g.Arrange(new Rect(g.DesiredSize));
        g.UpdateLayout();
        Save(g, Path.Combine(outDir, $"winshot-surveyorder-{dpi}.png"), dpi);
    }

    // ------------------------------------------------------------ experiments

    /// <summary>
    /// Which lever actually sharpens a WPF vector glyph inside a Viewbox? Six candidates,
    /// same six glyphs, 4x loupe. Throwaway evidence, gated on its own env var.
    /// </summary>
    private static void RenderExperiment(string outDir, int dpi)
    {
        string[] probes = { "SurveyIcon.Rect", "SurveyIcon.Ellipse", "SurveyIcon.Pixelate",
                            "SurveyIcon.Shapes", "SurveyIcon.Crop", "SurveyIcon.Pan" };
        (string Name, Action<Viewbox, ContentControl> Apply)[] variants =
        {
            ("shipped",        (v, c) => { }),
            ("snaps=false",    (v, c) => { v.SnapsToDevicePixels = false; c.SnapsToDevicePixels = false; }),
            ("snaps=true",     (v, c) => { v.SnapsToDevicePixels = true;  c.SnapsToDevicePixels = true; }),
            ("rounding=false", (v, c) => { v.UseLayoutRounding = false;   c.UseLayoutRounding = false; }),
            ("edge=aliased",   (v, c) => RenderOptions.SetEdgeMode(v, EdgeMode.Aliased)),
            ("clearTypeOff",   (v, c) => RenderOptions.SetClearTypeHint(v, ClearTypeHint.Auto)),
        };

        var g = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        foreach (var _ in variants) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18 * Magnify + 14) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        Put(g, Label($"{dpi} dpi"), 0, 0);
        for (int c = 0; c < variants.Length; c++) Put(g, Label(variants[c].Name), 0, c + 1);

        for (int r = 0; r < probes.Length; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18 * Magnify + 10) });
            Put(g, Label(probes[r].Substring("SurveyIcon.".Length)), r + 1, 0);
            for (int c = 0; c < variants.Length; c++)
                Put(g, Variant(probes[r], 18, dpi, variants[c].Apply), r + 1, c + 1);
        }

        g.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        g.Arrange(new Rect(g.DesiredSize));
        g.UpdateLayout();
        Save(g, Path.Combine(outDir, $"experiment-{dpi}.png"), dpi);
    }

    private static FrameworkElement Variant(
        string key, double size, int dpi, Action<Viewbox, ContentControl> apply)
    {
        var res = (FrameworkElement)Application.Current.TryFindResource(key)!;
        var cc = new ContentControl
        {
            Content = res,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
        };
        var host = new Viewbox { Width = size, Height = size, Child = cc };
        apply(host, cc);

        var wrap = new Grid { Width = size, Height = size };
        wrap.Children.Add(host);
        wrap.Measure(new Size(size, size));
        wrap.Arrange(new Rect(0, 0, size, size));
        wrap.UpdateLayout();
        int px = (int)Math.Round(size * dpi / 96.0);
        var rtb = new RenderTargetBitmap(px, px, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(wrap);
        var img = new Image
        {
            Source = rtb,
            Width = px * Magnify * 96.0 / dpi,
            Height = px * Magnify * 96.0 / dpi,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return Center(img);
    }

    // --------------------------------------------------------------- toolbars

    private static void RenderToolbars(string outDir)
    {
        var settings = new SettingsService();
        var history = new HistoryService(settings);
        var bmp = new SD.Bitmap(1100, 620);
        using (var g = SD.Graphics.FromImage(bmp)) g.Clear(SD.Color.FromArgb(0x33, 0x3A, 0x45));

        var editor = new EditorWindow(bmp, settings, history)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = -6000,
            ShowInTaskbar = false,
            Width = 1280,
            Height = 800,
        };
        editor.Show();
        Pump(45);

        var diag = new System.Text.StringBuilder();

        // Each group's sub-row, stacked into one strip per DPI so a human sees the whole
        // family at once rather than four files that have to be held in memory together.
        var groups = new (string Button, string Name)[]
        {
            ("DrawGroupBtn", "draw"),
            ("ShapeGroupBtn", "shapes"),
            ("TextGroupBtn", "text"),
            ("EffectsGroupBtn", "effects"),
            ("ImageGroupBtn", "image"),
        };

        foreach (int dpi in new[] { 96, 120, 144, 192 })
        {
            var strips = new List<BitmapSource>();
            var topBar = editor.FindName("TopToolbar") as FrameworkElement;

            foreach (var (btnName, _) in groups)
            {
                if (editor.FindName(btnName) is System.Windows.Controls.Primitives.ButtonBase b)
                {
                    b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump(6);
                }
                if (topBar is not null) strips.Add(Shot(topBar, dpi));
                if (editor.FindName("EditorGroupBar") is FrameworkElement bar
                    && bar.ActualWidth > 0 && bar.Visibility == Visibility.Visible)
                    strips.Add(Shot(bar, dpi));
            }

            foreach (string name in new[] { "ZoomRail", "EditorBottomBar" })
            {
                var fe = editor.FindName(name) as FrameworkElement;
                diag.AppendLine($"{dpi} {name}: found={fe is not null} " +
                                $"size={fe?.ActualWidth ?? -1}x{fe?.ActualHeight ?? -1} vis={fe?.Visibility}");
                if (fe is { ActualWidth: > 0, ActualHeight: > 0 })
                {
                    // Its own file as well as the stack: the rail is 48 x 709, so inside a
                    // stack of 1200-wide strips it is a sliver nobody can judge.
                    var shot = Shot(fe, dpi);
                    Write(shot, Path.Combine(outDir, $"{name.ToLowerInvariant()}-{dpi}.png"));
                    strips.Add(shot);
                }
            }

            SaveStack(strips, Path.Combine(outDir, $"toolbar-{dpi}.png"), dpi);
        }

        File.WriteAllText(Path.Combine(outDir, "toolbar-diag.txt"), diag.ToString());
        editor.Close();
        Pump(4);
    }

    /// <summary>
    /// Crop one element out of the live window.
    ///
    /// Via a VisualBrush, NOT `rtb.Render(element)` directly: RenderTargetBitmap.Render honours
    /// the visual's own OFFSET inside its parent, so an element that sits at x=1232 — the zoom
    /// rail, pinned to the right edge — draws itself 1232 px off the right edge of its own
    /// 72 px-wide bitmap and comes back empty. It looks like the element failed to render; it
    /// actually rendered perfectly, just outside the frame. A VisualBrush re-origins it at 0,0.
    /// </summary>
    private static BitmapSource Shot(FrameworkElement e, int dpi)
    {
        e.UpdateLayout();
        var size = e.RenderSize;
        int w = Math.Max(1, (int)Math.Ceiling(size.Width * dpi / 96));
        int h = Math.Max(1, (int)Math.Ceiling(size.Height * dpi / 96));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                             null, new Rect(0, 0, size.Width, size.Height));

        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static void SaveStack(List<BitmapSource> strips, string path, int dpi)
    {
        if (strips.Count == 0) return;
        int w = strips.Max(s => s.PixelWidth);
        int h = strips.Sum(s => s.PixelHeight + 6) + 6;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), null,
                new Rect(0, 0, w * 96.0 / dpi, h * 96.0 / dpi));
            double y = 6.0 * 96 / dpi;
            foreach (var s in strips)
            {
                dc.DrawImage(s, new Rect(0, y, s.PixelWidth * 96.0 / dpi, s.PixelHeight * 96.0 / dpi));
                y += (s.PixelHeight + 6) * 96.0 / dpi;
            }
        }
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        Write(rtb, path);
    }

    private static void Save(FrameworkElement e, string path, int dpi)
    {
        var size = e.DesiredSize;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi / 96), (int)Math.Ceiling(size.Height * dpi / 96),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(e);
        Write(rtb, path);
    }

    private static void Write(BitmapSource b, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Pump(int iterations)
    {
        for (int n = 0; n < iterations; n++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }
}
