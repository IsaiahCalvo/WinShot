using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WinShot.Editor;

/// <summary>
/// Survey's row 2 motion (utils/loadoutTransition.js attachRowCrossfade, RULED 2026-09-28
/// "one motion for row 2", w51 "downward swap"):
///
/// <list type="bullet">
/// <item>The SET of settings changes while the row is up: a still copy of the old row sinks
/// 7px and fades out over 120ms (ease-in), and the live row, already at its new centred spot,
/// comes down 6px into place and fades in over 150ms, 35ms later (ease-out). Both are clipped
/// to the row's 34px band.</item>
/// <item>The same set (pen to highlighter, a new colour): nothing moves.</item>
/// <item>The row goes away: a copy of it sinks and fades the same way.</item>
/// <item>A menu or popover is open from the row, or Windows has animations off: instant.</item>
/// </list>
///
/// Visibility itself still changes at once, so the row never lingers as something you can
/// click; only a picture of it fades.
/// </summary>
public partial class EditorWindow
{
    private static readonly IEasingFunction RowOutEase = new CubicBezierEase(0.32, 0, 0.67, 0);
    private static readonly IEasingFunction RowInEase = new CubicBezierEase(0.33, 1, 0.68, 1);
    private const double RowOutMs = 120, RowInMs = 150, RowInDelayMs = 35, RowOutTravel = 7, RowInTravel = 6;

    private Canvas? _rowGhostHost;
    private int _rowMotionDepth;

    private readonly record struct RowMotionState(
        bool WasShown, string Signature, ImageSource? Bar, ImageSource? Row, Point RowAt);

    /// <summary>Runs before UpdateContextPanels changes anything: what row 2 looks like now.</summary>
    private RowMotionState? BeginRowMotion()
    {
        if (_rowMotionDepth++ > 0) return null;
        if (!RowMotionAllowed()) return null;
        bool shown = EditorGroupBar.IsVisible && EditorGroupBar.ActualWidth > 0;
        if (!shown) return new RowMotionState(false, "", null, null, default);
        ImageSource? bar = Snapshot(EditorGroupBar);
        ImageSource? row = Snapshot(EditorFormatRow);
        Point at = EditorFormatRow.ActualWidth > 0
            ? EditorFormatRow.TranslatePoint(new Point(0, 0), EditorGroupBar)
            : default;
        return new RowMotionState(true, RowSignature(), bar, row, at);
    }

    /// <summary>Runs after UpdateContextPanels: plays the swap or the leave, if there is one.</summary>
    private void EndRowMotion(RowMotionState? before)
    {
        _rowMotionDepth = Math.Max(0, _rowMotionDepth - 1);
        if (before is not { WasShown: true } state) return;
        bool shown = EditorGroupBar.Visibility == Visibility.Visible;
        if (!shown)
        {
            if (state.Bar is not null) FadeGhost(state.Bar, new Point(0, 0), EditorGroupBar.ActualWidth);
            return;
        }
        if (RowSignature() == state.Signature || RowPopoverOpen()) return;
        if (state.Row is not null)
            FadeGhost(state.Row, state.RowAt, EditorFormatRow.ActualWidth);
        DropInLiveRow();
    }

    private bool RowMotionAllowed() =>
        IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation && !RowPopoverOpen()
        && EditorGroupBar is not null && EditorFormatRow is not null;

    /// <summary>The swap is instant while a menu hangs off the row, so it stays on its opener.</summary>
    private bool RowPopoverOpen()
    {
        if (ColorPickerPopup?.IsOpen == true || FormatOverflowPopup?.IsOpen == true) return true;
        return EditorFormatRow is not null && Descendants(EditorFormatRow).Any(d =>
            d is ComboBox { IsDropDownOpen: true } || d is Popup { IsOpen: true });
    }

    /// <summary>
    /// The set of controls on the row (Survey loadoutSignature): every element that is
    /// showing, walked only through showing parents. A new colour or a new number keeps the
    /// set, so it does not animate.
    /// </summary>
    private string RowSignature()
    {
        if (EditorFormatRow is null || EditorGroupBar.Visibility != Visibility.Visible) return "";
        var sb = new StringBuilder();
        void Walk(DependencyObject node)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
                sb.Append(RuntimeHelpers.GetHashCode(fe)).Append(',');
                if (fe is Panel or Decorator) Walk(fe);
            }
        }
        Walk(EditorFormatRow);
        return sb.ToString();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (DependencyObject deeper in Descendants(d)) yield return deeper;
        }
    }

    private static ImageSource? Snapshot(FrameworkElement el)
    {
        double w = el.ActualWidth, h = el.ActualHeight;
        if (w < 1 || h < 1) return null;
        DpiScale dpi = VisualTreeHelper.GetDpi(el);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w, h));
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A band-clipped layer over row 2 that holds the fading copies.</summary>
    private Canvas? RowGhostHost()
    {
        if (_rowGhostHost is not null) return _rowGhostHost;
        if (EditorGroupBar.Parent is not Panel parent) return null;
        _rowGhostHost = new Canvas
        {
            Height = EditorGroupBar.Height,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true,
            IsHitTestVisible = false,
            Focusable = false,
        };
        Grid.SetRow(_rowGhostHost, Grid.GetRow(EditorGroupBar));
        Grid.SetColumn(_rowGhostHost, Grid.GetColumn(EditorGroupBar));
        Grid.SetRowSpan(_rowGhostHost, Grid.GetRowSpan(EditorGroupBar));
        Grid.SetColumnSpan(_rowGhostHost, Grid.GetColumnSpan(EditorGroupBar));
        Panel.SetZIndex(_rowGhostHost, Panel.GetZIndex(EditorGroupBar) + 1);
        parent.Children.Insert(parent.Children.IndexOf(EditorGroupBar) + 1, _rowGhostHost);
        return _rowGhostHost;
    }

    private void FadeGhost(ImageSource picture, Point at, double width)
    {
        Canvas? host = RowGhostHost();
        if (host is null) return;
        var move = new TranslateTransform();
        var ghost = new Image
        {
            Source = picture,
            Width = picture.Width,
            Height = picture.Height,
            Stretch = Stretch.Fill,
            RenderTransform = move,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ghost, at.X);
        Canvas.SetTop(ghost, at.Y);
        host.Children.Add(ghost);
        var duration = new Duration(TimeSpan.FromMilliseconds(RowOutMs));
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = RowOutEase };
        fade.Completed += (_, _) => host.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, RowOutTravel, duration) { EasingFunction = RowOutEase });
    }

    private void DropInLiveRow()
    {
        if (EditorFormatRow.RenderTransform is not TranslateTransform move || move.IsFrozen)
        {
            move = new TranslateTransform();
            EditorFormatRow.RenderTransform = move;
        }
        static DoubleAnimationUsingKeyFrames Keys(double from, double to)
        {
            var keys = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            keys.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(RowInDelayMs))));
            keys.KeyFrames.Add(new EasingDoubleKeyFrame(to,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(RowInDelayMs + RowInMs)), RowInEase));
            return keys;
        }
        EditorFormatRow.BeginAnimation(OpacityProperty, Keys(0, 1));
        move.BeginAnimation(TranslateTransform.YProperty, Keys(-RowInTravel, 0));
    }

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2) as a WPF easing function.</summary>
    private sealed class CubicBezierEase : EasingFunctionBase
    {
        private readonly double _x1, _y1, _x2, _y2;

        public CubicBezierEase(double x1, double y1, double x2, double y2)
        {
            (_x1, _y1, _x2, _y2) = (x1, y1, x2, y2);
            EasingMode = EasingMode.EaseIn; // EaseInCore is the whole curve; EaseIn applies it as is.
        }

        protected override double EaseInCore(double t)
        {
            // Solve x(s) = t for s by bisection, then return y(s).
            double lo = 0, hi = 1, s = t;
            for (int i = 0; i < 24; i++)
            {
                s = (lo + hi) / 2;
                if (Bezier(s, _x1, _x2) < t) lo = s; else hi = s;
            }
            return Bezier(s, _y1, _y2);
        }

        private static double Bezier(double s, double p1, double p2)
        {
            double u = 1 - s;
            return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
        }

        protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);
    }
}
