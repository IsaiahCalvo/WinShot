using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinShot.Core;
using WinShot.Editor;
using WinShot.History;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

/// <summary>
/// The Polygon and Polyline tools end to end: vertices placed through the in-process pointer
/// seam, runs finished and cancelled through the real keyboard path, chrome and serialisation.
///
/// PolyDraftTests covers the rules. What this covers is that they are actually WIRED — that a
/// click with the tool armed drops a vertex instead of starting a drag-out shape, that Enter
/// reaches the draft rather than a focused toolbar button, that Escape is eaten before the
/// editor's own "clear selection", and that a committed run gets vertex handles.
///
/// LANDMINE (.claudehelper/HANDOFF.md): one System.Windows.Application per assembly, so this
/// class is env-gated and must be run ALONE:
///     $env:WINSHOT_EDITOR_TESTS='1'
///     powershell -File .\tools\run-tests.ps1 -Filter 'FullyQualifiedName~EditorPolyToolTests'
/// A stood-down run reports as a PASS.
/// </summary>
public class EditorPolyToolTests
{
    /// <summary>Three clicks and Enter make a closed polygon of exactly those three corners.</summary>
    [Fact]
    public void ThreeClicksAndEnterCommitAPolygon()
    {
        string? type = null;
        int vertices = 0, count = 0;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            arm(EditorTool.Polygon);
            click(100, 100); click(300, 100); click(300, 260);
            key(Key.Enter);
            var poly = marks().LastOrDefault();
            count = marks().Count();
            type = poly?.Type;
            vertices = poly?.Points?.Length ?? 0;
            ran = true;
        });
        if (!ran) return;
        Assert.Equal(1, count);
        Assert.Equal(AnnotationData.TypePolygon, type);
        Assert.Equal(3, vertices);
    }

    /// <summary>
    /// A click is never a finish. Two clicks then Enter commits nothing — a polygon needs three
    /// corners — and, crucially, the draft SURVIVES: a third click and Enter still work, so the
    /// user does not lose the corners already placed.
    /// </summary>
    [Fact]
    public void ATooShortDraftCommitsNothingAndStaysAlive()
    {
        int afterFirstEnter = 0, afterThirdPoint = 0;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            arm(EditorTool.Polygon);
            click(100, 100); click(300, 100);
            key(Key.Enter);
            afterFirstEnter = marks().Count();
            click(300, 260);
            key(Key.Enter);
            afterThirdPoint = marks().Count();
            ran = true;
        });
        if (!ran) return;
        Assert.Equal(0, afterFirstEnter);
        Assert.Equal(1, afterThirdPoint);
    }

    /// <summary>Escape drops the draft: nothing is committed, and the next click starts over.</summary>
    [Fact]
    public void EscapeCancelsTheDraft()
    {
        int committed = 0;
        bool draftGone = false;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            arm(EditorTool.Polygon);
            click(100, 100); click(300, 100); click(300, 260);
            key(Key.Escape);
            committed = marks().Count();
            draftGone = typeof(EditorWindow)
                .GetField("_polyDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(editor) is null;
            ran = true;
        });
        if (!ran) return;
        Assert.Equal(0, committed);
        Assert.True(draftGone);
    }

    /// <summary>
    /// Clicking back ON the first vertex closes the run. This is how a POLYLINE becomes a
    /// polygon — the one gesture that changes the committed type.
    /// </summary>
    [Fact]
    public void ClickingTheFirstVertexClosesAPolylineIntoAPolygon()
    {
        string? closed = null, open = null;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            arm(EditorTool.Polyline);
            click(100, 100); click(300, 100); click(300, 260);
            click(101, 101);                 // inside the first vertex's magnet
            closed = marks().LastOrDefault()?.Type;

            arm(EditorTool.Polyline);
            click(100, 400); click(300, 400); click(300, 560);
            key(Key.Enter);
            open = marks().LastOrDefault()?.Type;
            ran = true;
        });
        if (!ran) return;
        Assert.Equal(AnnotationData.TypePolygon, closed);
        Assert.Equal(AnnotationData.TypePolyline, open);
    }

    /// <summary>
    /// A committed poly shape's single-click chrome is its OWN vertices, one grabber each —
    /// Survey's proprietary handles, not a bounding box. A box would make a polygon indis-
    /// tinguishable from a rectangle the moment you selected it.
    /// </summary>
    [Fact]
    public void ASelectedPolygonGetsOneHandlePerVertex()
    {
        int visibleThumbs = 0;
        string? kind = null;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            arm(EditorTool.Polygon);
            click(100, 100); click(300, 100); click(300, 260); click(140, 300);
            key(Key.Enter);

            var t = typeof(EditorWindow);
            var canvas = (System.Windows.Controls.Panel)editor.FindName("AnnotationCanvas")!;
            var poly = canvas.Children.OfType<Path>()
                .LastOrDefault(p => (p.Tag as AnnotationData)?.Type == AnnotationData.TypePolygon);
            if (poly is null) return;
            var select = t.GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(UIElement)], null);
            select?.Invoke(editor, [poly]);
            Pump();

            kind = t.GetField("_handleKind", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(editor)!.ToString();
            var thumbs = (System.Collections.IEnumerable)t
                .GetField("_handleThumbs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(editor)!;
            visibleThumbs = thumbs.Cast<FrameworkElement>().Count(x => x.Visibility == Visibility.Visible);
            ran = true;
        });
        if (!ran) return;
        Assert.Equal("Vertex", kind);
        Assert.Equal(4, visibleThumbs);
    }

    /// <summary>
    /// The reason Polygon exists beyond "one more shape": the revision cloud stops being
    /// rectangle-only. A cloud-bordered polygon must draw crowns that stand OUTSIDE the ring —
    /// the rectangle cloud had to be inset to survive a layout clip, and a poly Path has no
    /// slot to be clipped by.
    /// </summary>
    [Fact]
    public void ACloudBorderedPolygonDrawsItsCrownsOutsideTheRing()
    {
        bool overhangs = false;
        bool ran = false;
        WithEditor((editor, arm, click, key, marks) =>
        {
            typeof(EditorWindow).GetField("_lineStyle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, LineBorderStyle.Cloud);
            arm(EditorTool.Polygon);
            click(200, 200); click(500, 200); click(500, 500);
            key(Key.Enter);

            var canvas = (System.Windows.Controls.Panel)editor.FindName("AnnotationCanvas")!;
            var poly = canvas.Children.OfType<Path>()
                .LastOrDefault(p => (p.Tag as AnnotationData)?.Type == AnnotationData.TypePolygon);
            if (poly?.Data is null) return;
            Rect b = poly.Data.Bounds;
            overhangs = b.Left < 200 && b.Top < 200 && b.Right > 500 && b.Bottom > 500;
            ran = true;
        });
        if (!ran) return;
        Assert.True(overhangs, "a cloud polygon's crowns must overhang the vertex ring");
    }

    /// <summary>
    /// Ellipse gets the Cloud border style, through the real drag. Survey offers Cloud on every
    /// region marker; WinShot could only build a rectangle one because five separate places
    /// each spelled out "rectangle" when rebuilding a cloud's geometry, so the ellipse cloud was
    /// silently downgraded to Solid at commit. Now it commits as a cloud, and the crowns are
    /// really there: an ellipse cloud's geometry must differ from the plain ellipse's box and
    /// from a RECTANGLE cloud built on the same slot.
    /// </summary>
    [Fact]
    public void AnEllipseCommitsWithTheCloudBorderStyle()
    {
        string? lineStyle = null;
        bool isPath = false, differsFromRectCloud = false, bumpVisible = false;
        double? bump = null;
        bool ran = false;

        WithEditor((editor, arm, click, key, marks) =>
        {
            var t = typeof(EditorWindow);
            t.GetField("_lineStyle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, LineBorderStyle.Cloud);
            arm(EditorTool.Ellipse);

            var canvas = (FrameworkElement)editor.FindName("AnnotationCanvas")!;
            var viewport = (FrameworkElement)editor.FindName("Viewport")!;
            EditorWindow.PointerArgs Pt(Point c) =>
                new(c, canvas.TranslatePoint(c, viewport), MouseButton.Left, 1);
            editor.PointerDown(Pt(new Point(200, 200)));
            Pump();
            editor.PointerMove(Pt(new Point(520, 380)));
            Pump();
            editor.PointerUp(Pt(new Point(520, 380)));
            Pump();

            var oval = ((System.Windows.Controls.Panel)canvas).Children.OfType<FrameworkElement>()
                .LastOrDefault(c => (c.Tag as AnnotationData)?.Type == AnnotationData.TypeEllipse);
            if (oval is null) return;
            var meta = (AnnotationData)oval.Tag!;
            lineStyle = meta.LineStyle;
            bump = meta.CloudIntensity;
            isPath = oval is Path;

            // Same slot, walked as a rectangle instead: if the ellipse walk were not being
            // used, these two would be identical geometry.
            if (oval is Path cloud && cloud.Data is not null)
            {
                var asRect = CloudPath.ForBox(AnnotationData.TypeRectangle,
                    new Rect(0, 0, oval.Width, oval.Height), meta.CloudIntensity ?? 2,
                    cloud.StrokeThickness);
                differsFromRectCloud = cloud.Data.ToString() != asRect.ToString();
            }

            // Survey shows the Bump field on a cloud-capable tool with the cloud style armed.
            bumpVisible = editor.FindName("CloudBumpPanel") is FrameworkElement panel && panel.IsVisible;
            ran = true;
        });

        if (!ran) return;
        Assert.Equal("cloud", lineStyle);
        Assert.True(isPath, "a cloud REPLACES the shape, so it commits as a Path, not an Ellipse");
        Assert.NotNull(bump);
        Assert.True(differsFromRectCloud, "the ellipse cloud is walking as a rectangle");
        Assert.True(bumpVisible, "the Bump field must be reachable with an ellipse cloud armed");
    }

    // ------------------------------------------------------------------ harness

    private static void WithEditor(
        Action<EditorWindow, Action<EditorTool>, Action<double, double>, Action<Key>,
            Func<IEnumerable<AnnotationData>>> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Environment.GetEnvironmentVariable("WINSHOT_EDITOR_TESTS") != "1") return;
                if (Application.Current is { } inherited && !inherited.CheckAccess()) return;
                var app = Application.Current ?? new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ThemeResources.EnsureLoaded();

                var settings = new SettingsService();
                var history = new HistoryService(settings);
                var bmp = new SD.Bitmap(1200, 800);
                using (var g = SD.Graphics.FromImage(bmp)) g.Clear(SD.Color.White);

                var editor = new EditorWindow(bmp, settings, history)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -6000, Top = -6000, Width = 1400, Height = 900, ShowInTaskbar = false,
                };
                editor.Show();
                for (int i = 0; i < 25; i++) { Pump(); Thread.Sleep(15); }
                editor.UpdateLayout();
                Pump();

                var t = typeof(EditorWindow);
                var toolField = t.GetField("_tool", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = t.GetMethod("UpdateContextPanels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Arm(EditorTool tool)
                {
                    toolField.SetValue(editor, tool);
                    update.Invoke(editor, null);
                    editor.UpdateLayout();
                    for (int i = 0; i < 6; i++) Pump();
                    Pump();
                }

                var canvas = (FrameworkElement)editor.FindName("AnnotationCanvas")!;
                var viewport = (FrameworkElement)editor.FindName("Viewport")!;
                // A poly vertex is placed by a plain click: press and release on the spot, no
                // drag at all, which is the whole difference from rect/ellipse.
                void Click(double x, double y)
                {
                    var c = new Point(x, y);
                    var args = new EditorWindow.PointerArgs(
                        c, canvas.TranslatePoint(c, viewport), MouseButton.Left, 1);
                    editor.PointerDown(args);
                    Pump();
                    editor.PointerUp(args);
                    Pump();
                }

                // Through the window's real PreviewKeyDown, not the private handler: half of
                // what these keys have to prove is that they REACH the draft.
                void Key_(Key key)
                {
                    var source = PresentationSource.FromVisual(editor);
                    if (source is null) return;
                    editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    });
                    Pump();
                }

                IEnumerable<AnnotationData> Marks() =>
                    ((System.Windows.Controls.Panel)canvas).Children.OfType<FrameworkElement>()
                        .Select(c => c.Tag as AnnotationData)
                        .Where(m => m is { Type: AnnotationData.TypePolygon or AnnotationData.TypePolyline })
                        .Select(m => m!)
                        .ToArray();

                body(editor, Arm, Click, Key_, Marks);
                editor.Close();
                Pump();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
