using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>A span the user froze with H or V. Screen px; one axis is the measurement,
/// the other only says where the line sits.</summary>
public readonly record struct MeasureHold(SD.Rectangle Bounds, bool Horizontal, SD.Point At);

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

    public byte Tolerance { get; private set; } = EdgeSnap.DefaultTolerance;

    /// <summary>Alt-click flips the readout between WCAG's ratio and APCA's Lc.</summary>
    public bool UseApca { get; private set; }

    public IReadOnlyList<MeasureHold> Holds => _holds;

    /// <summary>Vertical guides, by screen X. Sorted, so the pair distances below are adjacent pairs.</summary>
    public IReadOnlyList<int> GuidesX => _guidesX;

    /// <summary>Horizontal guides, by screen Y.</summary>
    public IReadOnlyList<int> GuidesY => _guidesY;

    public void Reset()
    {
        _holds.Clear();
        _guidesX.Clear();
        _guidesY.Clear();
        UseApca = false;
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

    public void Hold(SD.Rectangle bounds, bool horizontal, SD.Point at) =>
        _holds.Add(new MeasureHold(bounds, horizontal, at));

    /// <summary>Backspace: drop the most recent hold. False when there was nothing to drop.</summary>
    public bool RemoveLastHold()
    {
        if (_holds.Count == 0)
            return false;
        _holds.RemoveAt(_holds.Count - 1);
        return true;
    }

    public void AddGuideX(int x) => Insert(_guidesX, x);

    public void AddGuideY(int y) => Insert(_guidesY, y);

    private static void Insert(List<int> guides, int value)
    {
        if (guides.Contains(value))
            return;
        guides.Add(value);
        guides.Sort();
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

    public bool HasAnything => _holds.Count > 0 || _guidesX.Count > 0 || _guidesY.Count > 0;

    /// <summary>First Escape clears the canvas, second one leaves. Returns true when this
    /// press cleared something, i.e. when it must NOT also close the selector.</summary>
    public bool ClearOnEscape()
    {
        if (!HasAnything)
            return false;
        _holds.Clear();
        _guidesX.Clear();
        _guidesY.Clear();
        return true;
    }

    /// <summary>
    /// What Ctrl+C puts on the clipboard: every held span, newest last, one per line — or the
    /// live cross when nothing is held, because copying nothing after pressing copy is the
    /// worst possible answer.
    /// </summary>
    public string CopyText(SD.Rectangle? live, int dpi)
    {
        if (_holds.Count == 0)
        {
            return live is SD.Rectangle rect
                ? OneLine(MeasureFormat.CrossLabel(rect.Size, dpi))
                : string.Empty;
        }

        return string.Join(Environment.NewLine, _holds.Select(hold =>
            OneLine(MeasureFormat.SpanLabel(
                hold.Horizontal ? hold.Bounds.Width : hold.Bounds.Height, dpi))));
    }

    /// <summary>The pill wraps onto two or three lines; the clipboard should not.</summary>
    private static string OneLine(string label) => label.Replace("\n", " · ");
}
