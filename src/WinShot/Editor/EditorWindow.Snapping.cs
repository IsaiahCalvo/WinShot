using System.Windows;
using System.Windows.Input;

namespace WinShot.Editor;

/// <summary>
/// Survey's move snapping: while a selection is dragged, its edges and centre stick to the
/// edges and centres of the other marks and of the picture itself. Alt+drag moves without
/// snapping; Shift+S turns snapping off and on (remembered between sessions). The geometry is
/// <see cref="ObjectSnap"/>.
/// </summary>
public partial class EditorWindow
{
    /// <summary>Snap reach in screen pixels, so it feels the same at every zoom.</summary>
    private const double MoveSnapScreenPx = 6;

    // The pointer's own travel since the press. _moveTotal is what was actually applied,
    // which differs from this by the current snap.
    private Vector _moveRaw;
    private Rect? _snapStart;
    private List<Rect>? _snapOthers;

    private bool SnappingOn => _settings?.Current.EditorSnapping ?? true;

    private void ResetMoveSnap()
    {
        _moveRaw = new Vector();
        _snapStart = null;
        _snapOthers = null;
    }

    /// <summary>The move to apply for a pointer travel of <paramref name="raw"/>.</summary>
    private Vector SnappedMove(Vector raw)
    {
        if (!SnappingOn || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return raw;
        if (_snapStart is null)
        {
            Rect start = Rect.Empty;
            var moving = SelectionList();
            foreach (UIElement el in moving)
                if (AnnotationCanvas.Children.Contains(el)) start.Union(GetCanvasBounds(el));
            if (start.IsEmpty) return raw;
            // The live bounds already include whatever has been applied this gesture.
            start.Offset(-_moveTotal.X, -_moveTotal.Y);
            _snapStart = start;
            _snapOthers = new List<Rect>
            {
                new(0, 0, AnnotationCanvas.ActualWidth, AnnotationCanvas.ActualHeight),
            };
            foreach (UIElement child in AnnotationCanvas.Children)
            {
                if (moving.Contains(child) || child is not FrameworkElement { Tag: AnnotationData } fe) continue;
                if (fe.Visibility != Visibility.Visible) continue;
                Rect b = GetCanvasBounds(child);
                if (!b.IsEmpty) _snapOthers.Add(b);
            }
        }
        Rect candidate = _snapStart.Value;
        candidate.Offset(raw);
        double tolerance = MoveSnapScreenPx / Math.Max(0.01, _zoom);
        return raw + ObjectSnap.Adjust(candidate, _snapOthers!, tolerance);
    }

    private void ToggleSnapping()
    {
        if (_settings is null) return;
        _settings.Current.EditorSnapping = !_settings.Current.EditorSnapping;
        _settings.Save();
        App.Toast(_settings.Current.EditorSnapping ? "Snapping on" : "Snapping off",
            "Shift+S turns it back. Alt+drag moves one mark without snapping.");
    }
}
