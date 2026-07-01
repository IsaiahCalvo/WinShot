using System.Diagnostics;
using WF = System.Windows.Forms;

namespace WinShot.Core;

/// <summary>
/// Shared perf instrumentation. <see cref="TrackFirstShown"/> logs how long a
/// WinForms window took to become visible for the first time.
/// </summary>
public static class PerfLog
{
    /// <summary>
    /// Logs "Perf {metricName} first show: N ms" the first time <paramref name="form"/>
    /// becomes visible. Hooks both Shown and VisibleChanged so windows made visible
    /// without a Shown event (e.g. re-shown pooled dialogs) are still measured, then
    /// detaches both handlers so it fires exactly once.
    /// </summary>
    public static void TrackFirstShown(WF.Form form, string metricName)
    {
        var sw = Stopwatch.StartNew();
        bool logged = false;
        EventHandler? shownHandler = null;
        EventHandler? visibleHandler = null;

        void LogOnce()
        {
            if (logged) return;
            logged = true;
            if (shownHandler is not null)
                form.Shown -= shownHandler;
            if (visibleHandler is not null)
                form.VisibleChanged -= visibleHandler;
            Log.Info($"Perf {metricName} first show: {sw.ElapsedMilliseconds} ms");
        }

        shownHandler = (_, _) => LogOnce();
        visibleHandler = (_, _) =>
        {
            if (form.Visible)
                LogOnce();
        };
        form.Shown += shownHandler;
        form.VisibleChanged += visibleHandler;
        if (form.Visible)
            LogOnce();
    }
}
