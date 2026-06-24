using System.Windows;

namespace WinShot.Core;

public static class ThemeResources
{
    private const string ThemePath = "pack://application:,,,/WinShot;component/Theme/Theme.xaml";
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

        if (!app.Resources.MergedDictionaries.Any(d =>
                string.Equals(d.Source?.OriginalString, ThemePath, StringComparison.OrdinalIgnoreCase)))
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(ThemePath, UriKind.Absolute),
            });
        }

        _loaded = true;
    }
}
