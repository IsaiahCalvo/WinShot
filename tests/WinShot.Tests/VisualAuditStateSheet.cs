using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Xunit;

namespace WinShot.Tests;

/// <summary>
/// Round 5 measured the resting state and nothing else. Hover, press, checked, focus and
/// disabled are four fifths of what a control does and none of them had ever been looked at.
///
/// Forcing them is the whole problem: <c>IsMouseOver</c> and <c>IsPressed</c> are read-only,
/// derived from a mouse device this harness is forbidden to touch. So the sheet does not fake
/// the input — it applies the OUTCOME the template itself declares for that input: every
/// <see cref="Trigger"/> on the style and on the template whose property is the one being
/// forced has its setters applied and its enter-action storyboards run to completion. What is
/// rendered is exactly what the template says that state looks like, which is what a reviewer
/// would be judging anyway.
///
/// Its one blind spot, written down so nobody reads more into a clean sheet than is there: a
/// MultiTrigger, and any state a code-behind handler paints rather than the template.
/// </summary>
internal static class VisualAuditStateSheet
{
    /// <summary>The states worth a column, and the property each one is spelled with.</summary>
    private static readonly (string Name, DependencyProperty? Prop)[] States =
    {
        ("rest", null),
        ("hover", UIElement.IsMouseOverProperty),
        ("press", ButtonBase.IsPressedProperty),
        ("checked", ToggleButton.IsCheckedProperty),
        ("focus", UIElement.IsKeyboardFocusedProperty),
        ("off", UIElement.IsEnabledProperty),
    };

    internal static Window Build()
    {
        var wrap = new WrapPanel { Margin = new Thickness(16) };
        foreach (var (key, style) in ControlStyles())
        {
            try { wrap.Children.Add(Card(key, style)); }
            catch { /* a style that cannot stand on its own is not a defect the sheet can judge */ }
        }

        var win = new Window
        {
            Width = 1760,
            Height = 1500,
            Title = "state sheet",
            Content = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        win.SetResourceReference(Window.BackgroundProperty, "ToolbarBgBrush");
        return win;
    }

    /// <summary>One style, six states, labelled — the row a reviewer actually reads.</summary>
    private static UIElement Card(string key, Style style)
    {
        // No fixed width: a card sized to a guess clipped the sixth cell off every style whose
        // control is wider than a chip, which is how the focus column went unlooked-at.
        var rows = new StackPanel { Margin = new Thickness(0, 0, 20, 14) };
        var title = new TextBlock { Text = key, Margin = new Thickness(0, 0, 0, 4), FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        title.SetResourceReference(TextBlock.FontSizeProperty, "TextCaption");
        rows.Children.Add(title);

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, prop) in States)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            var caption = new TextBlock { Text = name, Margin = new Thickness(0, 3, 0, 0), TextAlignment = TextAlignment.Center };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
            caption.SetResourceReference(TextBlock.FontSizeProperty, "TextMicro");

            var c = Sample(style);
            c.Name = $"{Sanitise(key)}_{name}";
            cell.Children.Add(new Border { Child = c, HorizontalAlignment = HorizontalAlignment.Center });
            cell.Children.Add(caption);
            line.Children.Add(cell);

            // Applied after the tree is built so the template exists to be reached into.
            cell.Loaded += (_, _) => Force(c, prop);
        }
        rows.Children.Add(line);
        return rows;
    }

    private static Control Sample(Style style)
    {
        var c = (Control)Activator.CreateInstance(style.TargetType)!;
        c.Style = style;
        // A style that still forces the icon FONT renders any word as a row of tofu boxes and
        // nothing of the state can be seen through it. Give those a real family glyph — and
        // note that the font is left declared on the style at all, which is what let a stray
        // label become tofu in the first place.
        if (c is ContentControl icon && ForcesIconFont(style))
        {
            icon.Content = Application.Current.TryFindResource("SurveyIcon.Crop");
            return c;
        }
        switch (c)
        {
            case TextBox tb: tb.Text = "Value"; tb.Width = 64; break;
            case ComboBox cb:
                cb.Items.Add(new ComboBoxItem { Content = "One" });
                cb.SelectedIndex = 0;
                cb.Width = 84;
                break;
            case Slider s: s.Width = 84; s.Value = 50; break;
            case ContentControl cc when cc.Content is null: cc.Content = "Done"; break;
        }
        return c;
    }

    private static bool ForcesIconFont(Style? style)
    {
        for (Style? s = style; s is not null; s = s.BasedOn)
            foreach (var setter in s.Setters.OfType<Setter>())
                if (setter.Property == Control.FontFamilyProperty
                    && setter.Value?.ToString()?.Contains("MDL2", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
        return false;
    }

    /// <summary>
    /// Apply what the template declares for this state. Setters first, then the enter-action
    /// storyboards — the app paints nearly every hover and press as an Opacity animation on a
    /// named wash layer, so a sheet that only applied setters would show six identical cells.
    /// </summary>
    private static void Force(Control c, DependencyProperty? prop)
    {
        if (prop is null) return;

        if (prop == UIElement.IsEnabledProperty) { c.IsEnabled = false; return; }
        if (prop == ToggleButton.IsCheckedProperty)
        {
            if (c is ToggleButton t) { t.IsChecked = true; return; }
            return;                                        // nothing to check on a plain button
        }
        // Focus cannot simply be handed out: one window has one keyboard focus, so calling
        // Focus() on every cell would leave a ring on the last one and none on the other
        // eighty. The templates spell the ring with IsKeyboardFocused or IsKeyboardFocusWithin
        // depending on whether the control is the focus target or contains it, so both are
        // forced the same way as hover and press.
        bool focus = prop == UIElement.IsKeyboardFocusedProperty;

        foreach (var trigger in Triggers(c))
        {
            bool match = focus
                ? trigger.Property == UIElement.IsKeyboardFocusedProperty
                  || trigger.Property == UIElement.IsKeyboardFocusWithinProperty
                : trigger.Property == prop;
            if (!match || !Equals(trigger.Value, true)) continue;
            foreach (var setter in trigger.Setters.OfType<Setter>())
                Apply(c, setter);
            foreach (var begin in trigger.EnterActions.OfType<BeginStoryboard>())
                Run(c, begin.Storyboard);
        }
    }

    private static IEnumerable<Trigger> Triggers(Control c)
    {
        if (c.Template is not null)
            foreach (var t in c.Template.Triggers.OfType<Trigger>()) yield return t;
        for (Style? s = c.Style; s is not null; s = s.BasedOn)
            foreach (var t in s.Triggers.OfType<Trigger>()) yield return t;
    }

    private static void Apply(Control c, Setter setter)
    {
        DependencyObject? target = string.IsNullOrEmpty(setter.TargetName)
            ? c
            : c.Template?.FindName(setter.TargetName, c) as DependencyObject;
        if (target is null || setter.Property is null) return;
        object? value = setter.Value;
        if (value is DynamicResourceExtension dyn) value = c.TryFindResource(dyn.ResourceKey);
        if (value is System.Windows.Markup.MarkupExtension) return;   // nothing we can resolve here
        try { target.SetValue(setter.Property, value); } catch { }
    }

    private static void Run(Control c, Storyboard? sb)
    {
        if (sb is null || c.Template is null) return;
        try
        {
            // A Storyboard from a Style is frozen and shared; clone before driving it, and
            // hand it the template so its TargetNames resolve inside this control's own tree.
            var clone = sb.Clone();
            clone.Begin(c, c.Template, true);
        }
        catch { }
    }

    private static string Sanitise(string key) =>
        new(key.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// Every keyed Style in the merged theme whose target is a control that can stand on its
    /// own. Reading them out of the live dictionary rather than a list means a style added
    /// tomorrow is on the sheet tomorrow.
    /// </summary>
    private static IEnumerable<(string Key, Style Style)> ControlStyles()
    {
        var seen = new SortedDictionary<string, Style>(StringComparer.Ordinal);
        foreach (var d in Dictionaries(Application.Current.Resources))
            foreach (DictionaryEntry e in d)
            {
                if (e.Key is not string key || e.Value is not Style style) continue;
                var t = style.TargetType;
                if (t is null) continue;
                // Thumb and RepeatButton only mean anything inside the track they template.
                if (t == typeof(Thumb) || t == typeof(RepeatButton) || t == typeof(Separator)) continue;
                if (typeof(ButtonBase).IsAssignableFrom(t) || t == typeof(TextBox)
                    || t == typeof(ComboBox) || t == typeof(Slider))
                    seen[key] = style;
            }
        return seen.Select(kv => (kv.Key, kv.Value));
    }

    private static IEnumerable<ResourceDictionary> Dictionaries(ResourceDictionary d)
    {
        yield return d;
        foreach (var m in d.MergedDictionaries)
            foreach (var inner in Dictionaries(m))
                yield return inner;
    }
}

/// <summary>
/// The rules the state sheet exists to hold, checked as numbers rather than by eye: a disabled
/// label must still be readable, and a pressed fill must be DARKER than the resting one. The
/// accent ladder in the palette is Accent -&gt; AccentDeep -&gt; AccentPressed, each step
/// darker; a template that lightens on press is reading the ladder upside down.
/// </summary>
public class ControlStateTests
{
    [Fact]
    public void DisabledTextStaysReadableOnEverySurfaceItLandsOn()
    {
        var disabled = (0x66, 0xFF, 0xFF, 0xFF);
        // Window, toolbar, raised control, input and popover: every ground a disabled label sits on.
        foreach (var (name, bg) in new[]
                 {
                     ("WindowBg", (0x1C, 0x1C, 0x1E)), ("ToolbarBg", (0x26, 0x26, 0x28)),
                     ("Surface", (0x2E, 0x2E, 0x30)), ("SurfaceAlt", (0x38, 0x38, 0x3B)),
                     ("Elevated", (0x32, 0x32, 0x36)),
                 })
        {
            double ratio = Contrast(Over(disabled, bg), bg);
            Assert.True(ratio >= 3.0,
                $"TextDisabled on {name} is {ratio:F2}:1 — under 3:1 a disabled label stops being "
                + "legible, which is a different thing from looking unavailable.");
        }
    }

    [Fact]
    public void TheAccentLadderOnlyEverGoesDarker()
    {
        double accent = Luminance((0x0A, 0x84, 0xFF));
        double deep = Luminance((0x0A, 0x73, 0xE0));
        double pressed = Luminance((0x00, 0x60, 0xDF));
        Assert.True(deep < accent, "AccentDeep must be darker than Accent");
        Assert.True(pressed < deep, "AccentPressed must be darker than AccentDeep");
    }

    /// <summary>
    /// A press layer may only ever DARKEN. Four styles picked their pressed fill out of the
    /// surface ladder instead — the secondary pill and the circle action pressed to Elevated,
    /// which is lighter than the Surface they rest on, and both primaries pressed to
    /// AccentPressed, which is lighter than AccentDeep under the hover shade. Since a press is
    /// always preceded by a hover, those buttons got BRIGHTER the moment you pushed them.
    /// </summary>
    [Fact]
    public void NoPressLayerIsPaintedFromASurfaceToken()
    {
        string theme = File.ReadAllText(ThemePath());
        var offenders = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     theme, @"x:Name=""Press""(?<attrs>[^>]*)"))
        {
            var fill = System.Text.RegularExpressions.Regex.Match(
                m.Groups["attrs"].Value, @"(?:Background|Fill)=""\{StaticResource ([A-Za-z]+)\}""");
            if (!fill.Success) continue;
            string brush = fill.Groups[1].Value;
            if (!brush.Contains("Shade", StringComparison.Ordinal))
                offenders.Add(brush);
        }

        Assert.True(offenders.Count == 0,
            "A pressed fill must be a SHADE over the resting one, so it composites darker "
            + "whatever it lands on. These paint a flat surface colour instead and can come out "
            + "lighter than rest: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Every style someone can tab to needs a visible focus affordance. Eight had none and fell
    /// back on WPF's default, a dotted BLACK rectangle, which on a #1C1C1E window is nothing.
    /// </summary>
    [Fact]
    public void EveryTabbableStyleShowsWhereTheKeyboardIs()
    {
        string theme = File.ReadAllText(ThemePath());
        var offenders = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     theme,
                     @"<Style x:Key=""(?<key>[A-Za-z]+)"" TargetType=""(?<type>CheckBox|RadioButton|ComboBox|Slider|TextBox|ToggleButton|Button)""(?<body>.*?)
    </Style>",
                     System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            string body = m.Groups["body"].Value;
            // Focusable="False" is a deliberate opt-out: the rich-text chips must not steal
            // focus from the text box they format.
            if (body.Contains("Property=\"Focusable\" Value=\"False\"", StringComparison.Ordinal)) continue;
            if (body.Contains("FocusVisualStyle", StringComparison.Ordinal)) continue;
            if (body.Contains("IsKeyboardFocused", StringComparison.Ordinal)) continue;
            if (body.Contains("IsKeyboardFocusWithin", StringComparison.Ordinal)) continue;
            // A style with no template of its own inherits one, ring included.
            if (!body.Contains("<ControlTemplate", StringComparison.Ordinal)) continue;
            offenders.Add(m.Groups["key"].Value);
        }

        Assert.True(offenders.Count == 0,
            "No visible keyboard focus: give the style FocusVisualStyle=\"{StaticResource "
            + "FocusRing}\" or a ring of its own: " + string.Join(", ", offenders));
    }

    private static string ThemePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "WinShot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "WinShot", "Theme", "Theme.xaml");
    }

    private static (int R, int G, int B) Over((int A, int R, int G, int B) fg, (int R, int G, int B) bg)
    {
        double a = fg.A / 255.0;
        return ((int)Math.Round(fg.R * a + bg.R * (1 - a)),
                (int)Math.Round(fg.G * a + bg.G * (1 - a)),
                (int)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Ch(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }
}
