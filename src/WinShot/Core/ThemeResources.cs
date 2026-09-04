using System.Windows;

namespace WinShot.Core;

public static class ThemeResources
{
    /// <summary>Where the one shared dictionary lives; tests load it standalone from here.</summary>
    public const string ThemePath = "pack://application:,,,/WinShot;component/Theme/Theme.xaml";
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;

        var app = Application.Current;
        if (app is null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(EnsureLoaded);
            return;
        }

        if (_loaded) return;

        ResourceDictionary? theme = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            string.Equals(d.Source?.OriginalString, ThemePath, StringComparison.OrdinalIgnoreCase));

        if (theme is null)
        {
            theme = new ResourceDictionary { Source = new Uri(ThemePath, UriKind.Absolute) };
            app.Resources.MergedDictionaries.Add(theme);
        }

        ApplyAnimationPreference(theme);

        _loaded = true;
    }

    /// <summary>
    /// Honours Settings &gt; Accessibility &gt; Visual effects &gt; Animation effects on the
    /// WPF side. Core/Motion.cs already jumps straight to the end state when the user has
    /// animation off; the control templates' cross-fades had no equivalent, so with the
    /// setting off every WinForms control snapped while every WPF one still faded. Zeroing
    /// the shared storyboards' durations leaves the end states exactly as they were and
    /// removes the transition, which is what "off" means. Called on the loaded dictionary
    /// before any template is realised.
    /// </summary>
    internal static void ApplyAnimationPreference(ResourceDictionary theme)
    {
        if (Motion.AnimationsEnabled())
            return;

        foreach (object key in theme.Keys.Cast<object>().ToList())
        {
            if (theme[key] is not System.Windows.Media.Animation.Storyboard board)
                continue;

            // A Freezable in a dictionary may already be frozen; clone before editing.
            System.Windows.Media.Animation.Storyboard editable = board.IsFrozen ? board.Clone() : board;
            foreach (System.Windows.Media.Animation.Timeline child in editable.Children)
                child.Duration = new Duration(TimeSpan.Zero);

            theme[key] = editable;
        }
    }
}
