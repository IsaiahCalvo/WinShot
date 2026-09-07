using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WinShot.Core;
using Xunit;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Tests;

/// <summary>
/// The edge cases the Wave 0 review's breaker lens found by hand: degenerate toast columns,
/// clock holders after churn and mid-frame disposal, placement on a negative-Y monitor and
/// under an open quick-actions card, the segmented picker's contract when its cells change
/// out from under it, and — the one that catches a crash nothing else would — every trigger
/// storyboard in Theme.xaml resolving inside its own template.
/// </summary>
public class FoundationsBreakerTests
{
    private sealed class Anim : IDisposable
    {
        private readonly Func<bool> _prev;
        public Anim(bool on) { _prev = Motion.AnimationsEnabled; Motion.AnimationsEnabled = () => on; }
        public void Dispose() => Motion.AnimationsEnabled = _prev;
    }

    private static void PumpFor(int ms)
    {
        var d = Stopwatch.StartNew();
        while (d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void PumpUntil(Func<bool> c, int ms = 8000)
    {
        var d = Stopwatch.StartNew();
        while (!c() && d.ElapsedMilliseconds < ms) { WF.Application.DoEvents(); Thread.Sleep(1); }
    }

    // ---------------------------------------------------------- Trim / rows

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TrimTerminatesOnADegenerateColumn(int columnPx)
    {
        var clock = Stopwatch.StartNew();
        string cut = ToastWindow.Trim(
            "WinShot could not find an OCR language pack. Add one in Settings > Time & Language > Language & region.",
            columnPx);
        Assert.True(clock.ElapsedMilliseconds < 4000, $"Trim took {clock.ElapsedMilliseconds} ms at column {columnPx}");
        Assert.NotNull(cut);
    }

    [Fact]
    public void TrimHandlesWhitespaceOnlyAndControlCharacters()
    {
        Assert.Equal(string.Empty, ToastWindow.Trim("   \r\n\t  ", 244));
        Assert.Equal(string.Empty, ToastWindow.Trim(null!, 244));
        Assert.Equal(1, ToastWindow.LinesNeeded(string.Empty, 244));
        Assert.Equal(1, ToastWindow.LinesNeeded(null!, 244));
    }

    [Fact]
    public void TrimOfOneEnormousSpacelessTokenStillEnds()
    {
        var clock = Stopwatch.StartNew();
        string cut = ToastWindow.Trim(new string('W', 20000), 244);
        Assert.True(clock.ElapsedMilliseconds < 5000, $"{clock.ElapsedMilliseconds} ms");
        Assert.True(cut.Length < 600, $"len {cut.Length}");
    }

    // ---------------------------------------------------------- Toast churn

    [Fact]
    public void TwentyToastsInARowLeaveNoClockHolderAndOneLiveWindow()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var made = new List<ToastWindow>();
        for (int i = 0; i < 20; i++)
        {
            var t = new ToastWindow(
                $"Message {i}",
                "A body line long enough to wrap onto a second row in the toast's own column.",
                null,
                null,
                i % 3 == 0 ? ToastSeverity.Error : ToastSeverity.Success);
            t.Show();
            made.Add(t);
            PumpFor(5);
        }

        foreach (ToastWindow t in made) { t.Close(); t.Dispose(); }
        PumpUntil(() => Motion.Holders == 0, 4000);
        Assert.Equal(0, Motion.Holders);
        Assert.All(made, t => Assert.True(t.IsDisposed));
    }

    [Fact]
    public void AToastDisposedFromInsideItsOwnEntranceFrameReleasesTheClock()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        var t = new ToastWindow("Saved", "shot.png", null, null, ToastSeverity.Success);
        t.Shown += (_, _) => t.Dispose();
        t.Show();
        PumpUntil(() => Motion.Holders == 0, 4000);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void AToastAnchoredFarOffEveryMonitorStillLandsOnAMonitor()
    {
        using var _ = new Anim(false);
        var t = new ToastWindow("Copied", "The capture is on the clipboard.",
            new SD.Point(-40000, -40000), null, ToastSeverity.Success);
        t.Show();
        PumpFor(30);
        SD.Rectangle b = t.Bounds;
        bool onSomeScreen = WF.Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(b));
        t.Dispose();
        Assert.True(onSomeScreen, $"toast at {b} touched no monitor");
    }

    // ------------------------------------------------- corner vs card stack

    [Fact]
    public void CornerPlacementOnANegativeYMonitorStaysInsideIt()
    {
        var area = new SD.Rectangle(-1920, -1080, 1920, 1040);
        SD.Point p = ToastWindow.CornerPlacement(area, new SD.Size(300, 120), SD.Rectangle.Empty, 24, 12);
        Assert.True(p.X >= area.Left && p.X + 300 <= area.Right, $"x {p.X}");
        Assert.True(p.Y >= area.Top && p.Y + 120 <= area.Bottom, $"y {p.Y}");
    }

    [Fact]
    public void CornerPlacementWhenTheStackIsTallerThanTheScreenStaysOnScreen()
    {
        var area = new SD.Rectangle(0, 0, 1920, 1040);
        var cards = new SD.Rectangle(1600, -4000, 300, 5040);
        SD.Point p = ToastWindow.CornerPlacement(area, new SD.Size(300, 120), cards, 24, 12);
        Assert.True(p.Y >= area.Top, $"y {p.Y} above the working area");
    }

    // ------------------------------------------------------- DarkSegmented

    [Fact]
    public void SegmentedSelectionChangedThatReassignsOptionsDoesNotLoopOrLeak()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { ShowInTaskbar = false };
        var seg = new DarkSegmented { Options = ["One", "Two", "Three"], Size = new SD.Size(240, 30) };
        form.Controls.Add(seg);
        form.Show();

        int fired = 0;
        seg.SelectedIndexChanged += (_, _) =>
        {
            fired++;
            // The style-bar shape: a handler that rebuilds the picker from the new state.
            seg.Options = ["One", "Two"];
        };

        seg.SelectedIndex = 2;
        PumpFor(120);
        seg.SelectedIndex = 1;
        PumpFor(400);

        form.Close();
        PumpUntil(() => Motion.Holders == 0, 4000);
        Assert.Equal(0, Motion.Holders);
        Assert.True(fired >= 1);
        Assert.InRange(seg.SelectedIndex, 0, seg.Options.Length - 1);
    }

    [Fact]
    public void SegmentedHoverStormAcrossCellsThenDisposeReleasesTheClock()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { ShowInTaskbar = false };
        var seg = new DarkSegmented { Options = ["A", "B", "C", "D"], Size = new SD.Size(240, 30) };
        form.Controls.Add(seg);
        form.Show();

        for (int i = 0; i < 60; i++)
        {
            seg.GetType().GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(seg, [new WF.MouseEventArgs(WF.MouseButtons.None, 0, (i * 17) % 240, 10, 0)]);
            PumpFor(2);
        }

        form.Close();
        PumpUntil(() => Motion.Holders == 0, 4000);
        Assert.Equal(0, Motion.Holders);
    }

    [Fact]
    public void SegmentedOptionsSetToNullSurvivesPaintAndReportsNoSelection()
    {
        using var form = new WF.Form { ShowInTaskbar = false };
        var seg = new DarkSegmented { Options = ["A", "B"], Size = new SD.Size(120, 28) };
        form.Controls.Add(seg);
        form.Show();
        seg.SelectedIndex = 1;

        seg.Options = null!;
        seg.SelectedIndex = 0;
        seg.Invalidate();
        seg.Update();
        PumpFor(30);

        Assert.Empty(seg.Options);
        // 0 is a real cell everywhere else; a picker with no cells has to say so.
        Assert.Equal(-1, seg.SelectedIndex);
        form.Close();
    }

    /// <summary>
    /// A shorter array moves the selection. The owner has to hear about it, or a settings row
    /// keeps the value of a cell that is no longer under the pill.
    /// </summary>
    [Fact]
    public void SegmentedTellsItsOwnerWhenAShorterArrayMovesTheSelection()
    {
        using var seg = new DarkSegmented { Options = ["One", "Two", "Three"], Size = new SD.Size(240, 30) };
        seg.SelectedIndex = 2;

        int fired = 0;
        seg.SelectedIndexChanged += (_, _) => fired++;

        seg.Options = ["One", "Two"];
        Assert.Equal(1, seg.SelectedIndex);
        Assert.Equal(1, fired);

        // A clamp that changes nothing stays quiet.
        seg.Options = ["Uno", "Dos"];
        Assert.Equal(1, fired);
    }

    // ----------------------------------------------------------- DarkButton

    [Fact]
    public void DarkButtonEventStormThenDisposeReleasesTheClock()
    {
        using var _ = new Anim(true);
        Assert.Equal(0, Motion.Holders);

        using var form = new WF.Form { ShowInTaskbar = false };
        var b = new DarkButton { Text = "Open link", Size = new SD.Size(96, 26) };
        form.Controls.Add(b);
        form.Show();

        var enter = b.GetType().GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var leave = b.GetType().GetMethod("OnMouseLeave", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var down = b.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var up = b.GetType().GetMethod("OnMouseUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        for (int i = 0; i < 200; i++)
        {
            enter.Invoke(b, [EventArgs.Empty]);
            down.Invoke(b, [new WF.MouseEventArgs(WF.MouseButtons.Left, 1, 10, 10, 0)]);
            up.Invoke(b, [new WF.MouseEventArgs(WF.MouseButtons.Left, 1, 10, 10, 0)]);
            leave.Invoke(b, [EventArgs.Empty]);
        }

        form.Close();
        PumpUntil(() => Motion.Holders == 0, 4000);
        Assert.Equal(0, Motion.Holders);
    }

    // ------------------------------------------------------ ThemeResources

    private static void OnTheme(Action<ResourceDictionary> assertions)
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.ResourceAssembly;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                assertions(new ResourceDictionary
                {
                    Source = new Uri(ThemeResources.ThemePath, UriKind.Absolute),
                });
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ApplyAnimationPreferenceIsIdempotentAndZeroesNestedStoryboards()
    {
        using var _ = new Anim(false);
        OnTheme(theme =>
        {
        ThemeResources.ApplyAnimationPreference(theme);
        ThemeResources.ApplyAnimationPreference(theme);

        foreach (object key in theme.Keys.Cast<object>().ToList())
        {
            if (theme[key] is Storyboard sb)
                foreach (Timeline child in sb.Children)
                    Assert.Equal(TimeSpan.Zero, child.Duration.TimeSpan);
            if (theme[key] is Duration d)
                Assert.Equal(TimeSpan.Zero, d.TimeSpan);
        }
        });
    }

    [Fact]
    public void EveryTemplateTriggerStoryboardIsZeroedWithAnimationOff()
    {
        using var _ = new Anim(false);
        OnTheme(theme =>
        {
        ThemeResources.ApplyAnimationPreference(theme);

        var offenders = new List<string>();
        foreach (object key in theme.Keys.Cast<object>().ToList())
        {
            if (theme[key] is not Style style) continue;
            foreach (SetterBase sb in style.Setters)
            {
                if (sb is not Setter { Value: ControlTemplate t }) continue;
                foreach (TriggerBase tb in t.Triggers)
                {
                    foreach (TriggerActionCollection col in ActionsOf(tb))
                        foreach (TriggerAction a in col)
                            if (a is BeginStoryboard { Storyboard: { } board })
                                foreach (Timeline child in board.Children)
                                    if (child.Duration.HasTimeSpan && child.Duration.TimeSpan != TimeSpan.Zero)
                                        offenders.Add($"{key}: {child.Duration}");
                }
            }
        }
        Assert.Empty(offenders);
        });
    }

    private static IEnumerable<TriggerActionCollection> ActionsOf(TriggerBase trigger)
    {
        switch (trigger)
        {
            case Trigger t: yield return t.EnterActions; yield return t.ExitActions; break;
            case MultiTrigger mt: yield return mt.EnterActions; yield return mt.ExitActions; break;
            case DataTrigger dt: yield return dt.EnterActions; yield return dt.ExitActions; break;
            case MultiDataTrigger md: yield return md.EnterActions; yield return md.ExitActions; break;
            case EventTrigger et: yield return et.Actions; break;
        }
    }

    // ------------------------------------------------------- FriendlyError

    [Fact]
    public void FriendlyErrorSeesThroughTheWrappersTheAppActuallyThrows()
    {
        var disk = new System.IO.IOException("full", unchecked((int)0x80070070));
        Assert.Equal("The drive is full.", FriendlyError.For(disk));
        // TaskCanceledException derives from OperationCanceledException.
        Assert.Equal("That was cancelled.", FriendlyError.For(new TaskCanceledException()));
        // A Win32Exception must not claim an image failed.
        Assert.DoesNotContain("image", FriendlyError.For(new System.ComponentModel.Win32Exception(2)));
    }

    // ------------------------------------------------------- Scroll chrome

    [Fact]
    public void ScrollChromeForARegionOnEveryMonitorIsSizedAndBornThere()
    {
        foreach (WF.Screen s in WF.Screen.AllScreens)
        {
            SD.Rectangle b = s.Bounds;
            var region = new SD.Rectangle(b.X + 40, b.Y + 40, Math.Min(400, b.Width - 80), Math.Min(400, b.Height - 80));

            using var bar = new WinShot.Scrolling.ScrollControlsBar(region);
            using var panel = new WinShot.Scrolling.ScrollPreviewPanel(region);

            Assert.True(b.Contains(bar.Location), $"bar born at {bar.Location}, not on {b}");
            Assert.True(b.Contains(panel.Location), $"panel born at {panel.Location}, not on {b}");
            Assert.True(panel.Width > 0 && panel.Height > 0);
        }
    }

    // ---------------------------------------- live card stack vs the toast

    [Fact]
    public void ACornerToastNeverLandsOnAnOpenQuickActionsCard()
    {
        using var _ = new Anim(false);
        using var bitmap = new SD.Bitmap(600, 400);
        using var card = new WinShot.Overlay.FastQuickActionsWindow(bitmap, new SettingsService());
        card.Show();
        PumpFor(150);

        WF.Screen screen = WF.Screen.FromRectangle(card.Bounds);
        SD.Rectangle stack = WinShot.Overlay.FastQuickActionsWindow.StackBounds(screen.WorkingArea);
        Assert.False(stack.IsEmpty, "an open, visible card produced no stack bounds");

        var toast = new ToastWindow("Saved", "shot.png", null, null, ToastSeverity.Success);
        toast.Show();
        PumpFor(120);
        SD.Rectangle t = toast.Bounds;
        toast.Dispose();
        card.Close();
        PumpFor(50);

        Assert.False(t.IntersectsWith(stack), $"toast {t} overlaps the card stack {stack}");
    }

    // -------------------------------------------- mixed DPI body integrity

    [Fact]
    public void TheNoLanguagePackLineKeepsItsTailOnEveryMonitor()
    {
        using var _ = new Anim(false);
        const string line = "No OCR language pack is installed. Add one in Settings > Time & language.";
        foreach (WF.Screen s in WF.Screen.AllScreens)
        {
            var t = new ToastWindow("OCR unavailable", line,
                new SD.Point(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2),
                null, ToastSeverity.Error);
            t.Show();
            PumpFor(40);
            string shown = ToastWindow.Trim(line, t.BodyColumnPx);
            SD.Rectangle b = t.Bounds;
            t.Dispose();

            Assert.False(shown.EndsWith("…"), $"trimmed on {s.DeviceName} ({t.BodyColumnPx}px): '{shown}'");
            Assert.Contains("Time & language", shown);
            Assert.True(s.WorkingArea.Contains(b) || s.Bounds.IntersectsWith(b), $"toast {b} left {s.DeviceName}");
        }
    }

    [Fact]
    public void EveryFriendlyErrorLineFitsTheToastBodyOnEveryMonitor()
    {
        using var _ = new Anim(false);
        Exception[] cases =
        [
            new UnauthorizedAccessException(),
            new System.IO.DirectoryNotFoundException(),
            new System.IO.PathTooLongException(),
            new System.IO.IOException("x", unchecked((int)0x80070070)),
            new System.IO.FileNotFoundException(),
            new System.IO.IOException("x"),
            new System.Net.Http.HttpRequestException(),
            new TimeoutException(),
            new OperationCanceledException(),
            new OutOfMemoryException(),
            new System.Runtime.InteropServices.COMException("x", unchecked((int)0x800401D0)),
            new System.Runtime.InteropServices.ExternalException("x"),
            new ArgumentException("x"),
            new Exception("x"),
        ];

        var t = new ToastWindow("Save failed", "seed", null, null, ToastSeverity.Error);
        t.Show();
        PumpFor(30);
        int column = t.BodyColumnPx;
        t.Dispose();

        var clipped = new List<string>();
        foreach (Exception ex in cases)
        {
            string line = FriendlyError.For(ex);
            if (ToastWindow.Trim(line, column).EndsWith("…")) clipped.Add(line);
        }
        Assert.Empty(clipped);
    }


    // ------------------------------------------------ storyboard namescopes

    /// <summary>
    /// The shared HoverIn/PressIn/ChipIn/CheckIn storyboards address their layer by NAME.
    /// A template that uses one without carrying an element of that name throws
    /// InvalidOperationException the first time the trigger fires — on hover, in the
    /// installed app, with nothing in the suite to catch it. This begins every trigger
    /// storyboard against a realized instance of its own template.
    /// </summary>
    [Fact]
    public void EveryTriggerStoryboardResolvesInsideItsOwnTemplate()
    {
        using var _ = new Anim(true);
        var failures = new List<string>();

        OnTheme(theme =>
        {
            foreach (object key in theme.Keys.Cast<object>().ToList())
            {
                if (theme[key] is not Style style || style.TargetType is null) continue;

                ControlTemplate? template = style.Setters
                    .OfType<Setter>()
                    .Select(s => s.Value as ControlTemplate)
                    .FirstOrDefault(t => t is not null);
                if (template is null) continue;

                var boards = template.Triggers
                    .SelectMany(ActionsOf)
                    .SelectMany(c => c.OfType<BeginStoryboard>())
                    .Select(b => b.Storyboard)
                    .Where(b => b is not null)
                    .ToArray();
                if (boards.Length == 0) continue;

                object? instance;
                try { instance = Activator.CreateInstance(style.TargetType); }
                catch { continue; }
                if (instance is not FrameworkElement fe) continue;

                try
                {
                    fe.Style = style;
                    var host = new ContentControl { Content = fe, Width = 200, Height = 40 };
                    host.Measure(new Size(200, 40));
                    host.Arrange(new Rect(0, 0, 200, 40));
                    if (fe is Control c) c.ApplyTemplate();
                }
                catch (Exception ex)
                {
                    failures.Add($"{key}: realizing the template threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                foreach (Storyboard? board in boards)
                {
                    try { board!.Begin(fe, template, true); }
                    catch (Exception ex)
                    {
                        failures.Add($"{key}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        });

        Assert.Empty(failures);
    }

    /// <summary>Same check for the standalone ControlTemplate entries (ComboToggleTemplate
    /// and friends), which no Style setter points at from inside the dictionary.</summary>
    [Fact]
    public void EveryStandaloneTemplateStoryboardResolvesToo()
    {
        using var _ = new Anim(true);
        var failures = new List<string>();

        OnTheme(theme =>
        {
            foreach (object key in theme.Keys.Cast<object>().ToList())
            {
                if (theme[key] is not ControlTemplate template || template.TargetType is null) continue;

                var boards = template.Triggers
                    .SelectMany(ActionsOf)
                    .SelectMany(c => c.OfType<BeginStoryboard>())
                    .Select(b => b.Storyboard)
                    .Where(b => b is not null)
                    .ToArray();
                if (boards.Length == 0) continue;

                object? instance;
                try { instance = Activator.CreateInstance(template.TargetType); }
                catch { continue; }
                if (instance is not Control c) continue;

                try
                {
                    c.Template = template;
                    var host = new ContentControl { Content = c, Width = 200, Height = 40 };
                    host.Measure(new Size(200, 40));
                    host.Arrange(new Rect(0, 0, 200, 40));
                    c.ApplyTemplate();
                }
                catch (Exception ex)
                {
                    failures.Add($"{key}: realizing threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                foreach (Storyboard? board in boards)
                {
                    try { board!.Begin(c, template, true); }
                    catch (Exception ex) { failures.Add($"{key}: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        });

        Assert.Empty(failures);
    }

}
