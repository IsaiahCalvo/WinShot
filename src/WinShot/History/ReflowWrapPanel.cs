using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinShot.Core;

namespace WinShot.History;

/// <summary>
/// The history grid's panel. Lays out exactly like a <see cref="WrapPanel"/>, then slides each
/// card from where it used to be to where it now is instead of letting it jump — so filtering
/// the grid, or deleting a card out of the middle of a row, reads as the survivors closing the
/// gap rather than the whole page being rebuilt.
///
/// One tween drives every card: the offsets are render transforms, which do not feed back into
/// layout, so the pass cannot re-enter arrange. Resizing the window re-wraps every row for
/// reasons that have nothing to do with the content, so a width change is recorded silently
/// and never animated.
/// </summary>
internal sealed class ReflowWrapPanel : WrapPanel
{
    /// <summary>Long enough to read as one motion across a row, short enough not to sit in
    /// front of the next click.</summary>
    internal const int ReflowMs = 200;

    private readonly Dictionary<UIElement, Rect> _slots = new();
    private double _lastWidth = double.NaN;
    private IDisposable? _running;

    /// <summary>How many cards the last arrange pass had to move. Zero when nothing shifted,
    /// and zero for a resize, which is deliberately not animated.</summary>
    internal int LastReflowCount { get; private set; }

    /// <summary>How many cards the last arrange pass faded in because they were not there before.</summary>
    internal int LastEnterCount { get; private set; }

    internal bool IsReflowing => _running is not null;

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);

        // Only the WIDTH decides where cards wrap, and only the user resizing the window changes
        // it. The panel's height follows its content, so comparing the whole size would classify
        // every add and remove as a resize and animate nothing.
        bool sameViewport = finalSize.Width.Equals(_lastWidth);
        _lastWidth = finalSize.Width;

        var moves = new List<Move>();
        var entering = new List<UIElement>();
        var current = new Dictionary<UIElement, Rect>(InternalChildren.Count);

        foreach (UIElement child in InternalChildren)
        {
            Rect slot = child is FrameworkElement element
                ? LayoutInformation.GetLayoutSlot(element)
                : Rect.Empty;
            current[child] = slot;

            if (!_slots.TryGetValue(child, out Rect previous))
            {
                // A card that was not here last pass has nowhere to slide from, so it fades in.
                if (sameViewport) entering.Add(child);
                continue;
            }

            child.Opacity = 1;
            if (!sameViewport) continue;

            TranslateTransform offset = OffsetOf(child);
            // Measure from where the card actually IS, not from where it was laid out: a card
            // caught mid-slide is still carrying part of its last offset, and starting the new
            // slide from its layout slot would snap it there first.
            double dx = previous.X + offset.X - slot.X;
            double dy = previous.Y + offset.Y - slot.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            {
                offset.X = 0;
                offset.Y = 0;
                continue;
            }

            offset.X = dx;
            offset.Y = dy;
            moves.Add(new Move(offset, dx, dy));
        }

        _slots.Clear();
        foreach (KeyValuePair<UIElement, Rect> entry in current)
            _slots[entry.Key] = entry.Value;

        LastReflowCount = moves.Count;
        LastEnterCount = entering.Count;
        if (moves.Count == 0 && entering.Count == 0)
        {
            _running?.Dispose();
            _running = null;
            return result;
        }

        foreach (UIElement child in entering)
            child.Opacity = 0;

        _running?.Dispose();
        // With animation off system-wide Motion.Run applies the end state and finishes before it
        // returns, so the handle it hands back must not be recorded as a live tween.
        bool finished = false;
        IDisposable handle = Motion.Run(
            ReflowMs,
            Motion.EaseOutCubic,
            progress =>
            {
                double remaining = 1d - progress;
                foreach (Move move in moves)
                {
                    move.Offset.X = move.Dx * remaining;
                    move.Offset.Y = move.Dy * remaining;
                }

                foreach (UIElement child in entering)
                    child.Opacity = progress;
            },
            () =>
            {
                finished = true;
                _running = null;
            });
        _running = finished ? null : handle;

        return result;
    }

    private static TranslateTransform OffsetOf(UIElement child)
    {
        if (child.RenderTransform is TranslateTransform existing && !existing.IsFrozen)
            return existing;

        var offset = new TranslateTransform();
        child.RenderTransform = offset;
        return offset;
    }

    private readonly record struct Move(TranslateTransform Offset, double Dx, double Dy);
}
