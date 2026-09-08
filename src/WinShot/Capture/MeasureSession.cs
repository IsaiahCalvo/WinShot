using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>A span the user froze with H or V. Screen px; one axis is the measurement,
/// the other only says where the line sits. <paramref name="Dpi"/> is the DPI of the monitor
/// it was held on, carried on the hold itself so the clipboard and the label agree: a span
/// held on a 96 dpi secondary must read the same in both, whichever pane is painting it and
/// wherever the pointer has since walked.</summary>
public readonly record struct MeasureHold(SD.Rectangle Bounds, bool Horizontal, SD.Point At, int Dpi);

/// <summary>
/// Everything Measure mode remembers between mouse-moves: the tolerance knob, the spans the
/// user held, the guides they placed, and which contrast scale they are reading. No GDI, no
/// window, no cursor — so all of it is testable without a screen.
/// </summary>
internal sealed class MeasureSession
{
    /// <summary>PowerToys' wheel step. Big enough that one notch visibly changes the answer,
    /// which is the only way a person learns what the knob does.</summary>
    public const int ToleranceStep = 15;

    private readonly List<MeasureHold> _holds = new();
    private readonly List<int> _guidesX = new();
    private readonly List<int> _guidesY = new();

    /// <summary>What was placed, newest last. Holds go on the end of <see cref="_holds"/> and
    /// guides land wherever their coordinate sorts, so neither list remembers the order the
    /// two were interleaved in — and Backspace has to undo the last THING, not the last hold.
    /// A guide entry carries its axis and its current coordinate, which <see cref="MoveGuide"/>
    /// keeps up to date.</summary>
    private readonly List<(bool Guide, bool Vertical, int At)> _placed = new();

    public byte Tolerance { get; private set; } = EdgeSnap.DefaultTolerance;

    /// <summary>Alt-click flips the readout between WCAG's ratio and APCA's Lc.</summary>
    public bool UseApca { get; private set; }

    public IReadOnlyList<MeasureHold> Holds => _holds;

    /// <summary>Vertical guides, by screen X. Sorted, so the pair distances below are adjacent pairs.</summary>
    public IReadOnlyList<int> GuidesX => _guidesX;

    /// <summary>Horizontal guides, by screen Y.</summary>
    public IReadOnlyList<int> GuidesY => _guidesY;

    /// <summary>What the last Escape emptied, so Backspace can put it back. Escape is exactly
    /// the slip the Backspace comment argues against — one press and an hour of measuring is
    /// gone with nothing left in <see cref="_placed"/> to undo.</summary>
    private (List<MeasureHold> Holds, List<int> X, List<int> Y,
        List<(bool Guide, bool Vertical, int At)> Placed)? _cleared;

    public void Reset()
    {
        Clear();
        _cleared = null;
        UseApca = false;
    }

    private void Clear()
    {
        _holds.Clear();
        _guidesX.Clear();
        _guidesY.Clear();
        _placed.Clear();
    }

    public void SetTolerance(byte tolerance) => Tolerance = tolerance;

    /// <summary>Wheel up loosens, wheel down tightens. Returns false at the ends so the
    /// caller can skip a repaint that would change nothing.</summary>
    public bool AdjustTolerance(int wheelDelta)
    {
        int next = Math.Clamp(Tolerance + (wheelDelta > 0 ? ToleranceStep : -ToleranceStep), 0, 255);
        if (next == Tolerance)
            return false;
        Tolerance = (byte)next;
        return true;
    }

    public bool ToggleContrastScale()
    {
        UseApca = !UseApca;
        return UseApca;
    }

    /// <summary>Freezes a span. False when the identical span is already held, which is the
    /// same rejection <see cref="Insert"/> makes for guides and for the same reason: two holds
    /// with the same bounds on the same axis draw the same hairline, the same serifs and the
    /// same pill on the same pixels, so N of them are pixel-identical to one — and Backspace
    /// then removes them one press at a time with nothing on screen changing.</summary>
    public bool Hold(SD.Rectangle bounds, bool horizontal, SD.Point at, int dpi)
    {
        if (_holds.Any(h => h.Bounds == bounds && h.Horizontal == horizontal))
            return false;
        _holds.Add(new MeasureHold(bounds, horizontal, at, dpi));
        _placed.Add((Guide: false, Vertical: false, At: 0));
        return true;
    }

    /// <summary>Drops the most recent hold. False when there was nothing to drop.</summary>
    public bool RemoveLastHold()
    {
        if (_holds.Count == 0)
            return false;
        int index = _placed.FindLastIndex(p => !p.Guide);
        if (index < 0)
            return false;   // the two lists desynced; the contract here is "false", not a crash
        _holds.RemoveAt(_holds.Count - 1);
        _placed.RemoveAt(index);
        return true;
    }

    /// <summary>Drops the most recently placed guide, whichever axis it is on. Without this
    /// one misplaced guide could only be undone by clearing the whole canvas.</summary>
    public bool RemoveLastGuide()
    {
        int index = _placed.FindLastIndex(p => p.Guide);
        if (index < 0)
            return false;
        (_, bool vertical, int at) = _placed[index];
        _placed.RemoveAt(index);
        (vertical ? _guidesX : _guidesY).Remove(at);
        return true;
    }

    /// <summary>Backspace: remove the newest thing placed, hold or guide. One key for one
    /// idea — the user does not think of the two as separate stacks.</summary>
    public bool RemoveLast()
    {
        if (_placed.Count == 0)
            return false;
        return _placed[^1].Guide ? RemoveLastGuide() : RemoveLastHold();
    }

    /// <summary>Places a vertical guide. False when one already sits on that column, which is
    /// not a corner case: guide coordinates go through SnapCoordinate first, so two presses
    /// anywhere inside the snap band of the same window edge collapse onto one column.</summary>
    public bool AddGuideX(int x) => Insert(_guidesX, vertical: true, x);

    /// <summary>Places a horizontal guide. False when one already sits on that row.</summary>
    public bool AddGuideY(int y) => Insert(_guidesY, vertical: false, y);

    private bool Insert(List<int> guides, bool vertical, int value)
    {
        if (guides.Contains(value))
            return false;
        guides.Add(value);
        guides.Sort();
        _placed.Add((Guide: true, vertical, value));
        return true;
    }

    /// <summary>Moves the guide at <paramref name="from"/> to <paramref name="to"/>, keeping
    /// the list sorted. False when there is no guide there, or when a guide already sits on
    /// the destination — one guide per coordinate is the same invariant <see cref="Insert"/>
    /// holds, and a stacked pair would label its own zero-wide gap and then hand every drag
    /// to whichever of the two the search happened to find first.</summary>
    public bool MoveGuide(bool vertical, int from, int to)
    {
        List<int> guides = vertical ? _guidesX : _guidesY;
        if (guides.Contains(to))
            return false;
        int at = guides.IndexOf(from);
        if (at < 0)
            return false;
        guides[at] = to;
        guides.Sort();
        int placed = _placed.FindIndex(p => p.Guide && p.Vertical == vertical && p.At == from);
        if (placed >= 0)
            _placed[placed] = (true, vertical, to);
        return true;
    }

    /// <summary>The guide nearest <paramref name="value"/> within <paramref name="tolerance"/>
    /// px, or null. This is the drag hit-test.</summary>
    public int? GuideNear(bool vertical, int value, int tolerance)
    {
        List<int> guides = vertical ? _guidesX : _guidesY;
        int? best = null;
        int bestGap = tolerance + 1;
        foreach (int guide in guides)
        {
            int gap = Math.Abs(guide - value);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = guide;
            }
        }
        return best;
    }

    /// <summary>
    /// Adjacent guide pairs on one axis. A parallel pair labels its own gap for free — that
    /// is the entire reason a second guide is worth placing.
    /// </summary>
    public static IEnumerable<(int From, int To)> Pairs(IReadOnlyList<int> guides)
    {
        for (int i = 1; i < guides.Count; i++)
            yield return (guides[i - 1], guides[i]);
    }

    public bool HasGuides => _guidesX.Count > 0 || _guidesY.Count > 0;

    public bool HasAnything => _holds.Count > 0 || HasGuides;

    /// <summary>First Escape clears the canvas, second one leaves. Returns true when this
    /// press cleared something, i.e. when it must NOT also close the selector.</summary>
    public bool ClearOnEscape()
    {
        if (!HasAnything)
            return false;
        _cleared = (new List<MeasureHold>(_holds), new List<int>(_guidesX),
            new List<int>(_guidesY), new List<(bool, bool, int)>(_placed));
        Clear();
        return true;
    }

    /// <summary>Puts back what the last Escape emptied. False when there is nothing to put
    /// back, or when the canvas is not empty — a restore is an undo of the clear, not a merge
    /// on top of whatever has been measured since.</summary>
    public bool RestoreCleared()
    {
        if (_cleared is not (var holds, var x, var y, var placed) || HasAnything)
            return false;
        _holds.AddRange(holds);
        _guidesX.AddRange(x);
        _guidesY.AddRange(y);
        _placed.AddRange(placed);
        _cleared = null;
        return true;
    }

    /// <summary>
    /// What Ctrl+C puts on the clipboard: every held span, newest last, one per line — or the
    /// live cross when nothing is held, because copying nothing after pressing copy is the
    /// worst possible answer. <paramref name="liveDpi"/> applies only to the live cross; each
    /// hold is converted with the DPI of the monitor it was taken on, so the clipboard cannot
    /// contradict the label the user is looking at on a mixed-DPI desktop.
    /// </summary>
    public string CopyText(SD.Rectangle? live, int liveDpi)
    {
        if (_holds.Count == 0)
        {
            return live is SD.Rectangle rect
                ? OneLine(MeasureFormat.CrossLabel(rect.Size, liveDpi))
                : string.Empty;
        }

        return string.Join(Environment.NewLine, _holds.Select(hold =>
            OneLine(MeasureFormat.SpanLabel(
                hold.Horizontal ? hold.Bounds.Width : hold.Bounds.Height, hold.Dpi))));
    }

    /// <summary>The pill wraps onto two or three lines; the clipboard should not.</summary>
    private static string OneLine(string label) => label.Replace("\n", " · ");
}
