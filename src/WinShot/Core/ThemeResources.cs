using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

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

        ResourceDictionary? theme = Find(app.Resources);

        if (theme is null)
        {
            theme = new ResourceDictionary { Source = new Uri(ThemePath, UriKind.Absolute) };
            app.Resources.MergedDictionaries.Add(theme);
        }

        ApplyAnimationPreference(theme);

        _loaded = true;
    }

    /// <summary>
    /// The dictionary App.xaml already merged, or null. Finding it is the whole job:
    /// failing to find it makes EnsureLoaded ADD a second copy, zero that copy, and leave
    /// every window resolving against the original — animation-off would quietly stop
    /// working on the WPF side with nothing to show for it. The search is recursive because
    /// the merge is already nested one level (App.xaml wraps it in a ResourceDictionary)
    /// and nothing stops it being nested deeper.
    /// </summary>
    internal static ResourceDictionary? Find(ResourceDictionary owner)
    {
        foreach (ResourceDictionary merged in owner.MergedDictionaries)
        {
            if (string.Equals(merged.Source?.OriginalString, ThemePath, StringComparison.OrdinalIgnoreCase))
                return merged;
            if (Find(merged) is { } nested)
                return nested;
        }

        return null;
    }

    /// <summary>
    /// Honours Settings &gt; Accessibility &gt; Visual effects &gt; Animation effects on the
    /// WPF side. Core/Motion.cs already jumps straight to the end state when the user has
    /// animation off; the control templates' cross-fades had no equivalent, so with the
    /// setting off every WinForms control snapped while every WPF one still faded.
    ///
    /// Zeroing the shared storyboards' durations leaves the end states exactly as they were
    /// and removes the transition, which is what "off" means — but it has to be done in TWO
    /// places. A <c>{StaticResource HoverIn}</c> inside a ControlTemplate trigger resolves
    /// against a snapshot taken when the dictionary was parsed and holds that Storyboard
    /// INSTANCE for the life of the template, so writing a zeroed clone back into
    /// theme["HoverIn"] changes the dictionary entry and nothing else: every template keeps
    /// running the original 140 ms cross-fade. The storyboards come out of the dictionary
    /// frozen and cannot be edited in place, so the zeroed clone has to be pushed into every
    /// BeginStoryboard that holds the original too.
    ///
    /// Reaching those means realising the dictionary's Styles and templates. On a machine
    /// with animation ON this method returns before touching a single key; on one with it
    /// off, inflating ~146 deferred values once at startup is what the setting costs.
    ///
    /// App.OnStartup calls EnsureLoaded before the first window exists, so the actions are
    /// normally still unsealed and can be repointed. A sealed one (a style already applied
    /// to a live control) cannot be, so the zeroing is done in place wherever the storyboard
    /// itself is editable: that reaches sealed holders too, since they keep the instance.
    ///
    /// The preference is read once per process. Motion.Run re-reads it per tween, so the
    /// WinForms stack follows a mid-session toggle and the WPF stack needs a restart.
    /// </summary>
    internal static void ApplyAnimationPreference(ResourceDictionary theme)
    {
        if (Motion.AnimationsEnabled())
            return;

        var zeroed = new Dictionary<Storyboard, Storyboard>();

        foreach (object key in theme.Keys.Cast<object>().ToList())
        {
            switch (theme[key])
            {
                case Storyboard board:
                    theme[key] = Zero(board, zeroed);
                    break;
                case Style style:
                    ZeroTriggers(style.Triggers, zeroed);
                    foreach (SetterBase setterBase in style.Setters)
                        if (setterBase is Setter { Value: ControlTemplate styleTemplate })
                            ZeroTriggers(styleTemplate.Triggers, zeroed);
                    break;
                case ControlTemplate template:
                    ZeroTriggers(template.Triggers, zeroed);
                    break;
                // MotionQuick/Swap/Enter. The theme's own storyboards resolved these when the
                // dictionary was parsed, so zeroing the key does nothing for them - that is
                // what the two passes above are for. It is the storyboards declared inline in
                // a WINDOW's XAML that this reaches: HistoryWindow's overlay-action fades are
                // not dictionary keys, so nothing above walks them, and they kept animating
                // with the setting off. A window is parsed long after OnStartup runs this, so
                // its {StaticResource MotionQuick} picks up the zeroed value.
                case Duration:
                    theme[key] = new Duration(TimeSpan.Zero);
                    break;
            }
        }
    }

    /// <summary>The instant twin of a storyboard, made once and shared by every holder.</summary>
    private static Storyboard Zero(Storyboard board, Dictionary<Storyboard, Storyboard> cache)
    {
        if (cache.TryGetValue(board, out Storyboard? existing))
            return existing;

        // A Freezable in a dictionary is normally already frozen; clone before editing.
        Storyboard editable = board.IsFrozen ? board.Clone() : board;
        foreach (Timeline child in editable.Children)
            child.Duration = new Duration(TimeSpan.Zero);

        cache[board] = editable;
        return editable;
    }

    /// <summary>
    /// Repoints every BeginStoryboard in a trigger collection at the instant twin of the
    /// storyboard it currently holds. A sealed action belongs to a style already in use and
    /// cannot be repointed, and the storyboard it holds is frozen, so it cannot be edited in
    /// place either — running before the first window is the whole guarantee.
    /// </summary>
    private static void ZeroTriggers(TriggerCollection triggers, Dictionary<Storyboard, Storyboard> cache)
    {
        foreach (TriggerBase trigger in triggers)
        {
            foreach (TriggerActionCollection actions in ActionsOf(trigger))
            {
                foreach (TriggerAction action in actions)
                {
                    if (action is not BeginStoryboard { Storyboard: { } board } begin)
                        continue;

                    if (begin.IsSealed)
                    {
                        // Nothing can be done here: the action belongs to a template that is
                        // already in use, and the storyboard it holds came out of the
                        // dictionary frozen, so neither the holder nor the timeline can be
                        // edited. The only cure is running earlier, which App.OnStartup now
                        // guarantees — log rather than fail silently if that ever regresses.
                        Log.Error("Animation preference reached a sealed template; EnsureLoaded ran too late");
                        continue;
                    }

                    begin.Storyboard = Zero(board, cache);
                }
            }
        }
    }

    private static IEnumerable<TriggerActionCollection> ActionsOf(TriggerBase trigger)
    {
        switch (trigger)
        {
            case Trigger t:
                yield return t.EnterActions;
                yield return t.ExitActions;
                break;
            case MultiTrigger mt:
                yield return mt.EnterActions;
                yield return mt.ExitActions;
                break;
            case DataTrigger dt:
                yield return dt.EnterActions;
                yield return dt.ExitActions;
                break;
            case MultiDataTrigger mdt:
                yield return mdt.EnterActions;
                yield return mdt.ExitActions;
                break;
            case EventTrigger et:
                yield return et.Actions;
                break;
        }
    }
}
