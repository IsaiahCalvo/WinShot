using System.Drawing.Drawing2D;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// One row of the quick-actions card's context menu, drawn end to end rather than by
/// the stock menu-item layout. The stock one parks icons in a gutter to the left of
/// every row's text, so the menu reads as two left edges. Here every row's leading
/// content — a glyph, or the text on the rows that have no glyph — starts on the same
/// left edge, and only an iconed row's text is indented past its glyph.
/// </summary>
internal sealed class QuickActionMenuItem : WF.ToolStripMenuItem
{
    internal const int GlyphSize = 16;
    private const int PadLeft = 5;
    private const int PadRight = 9;
    private const int GlyphGap = 8;
    private const int ShortcutGap = 16;
    private const int ArrowWidth = 14;
    private const int MinHeight = 21;

    private readonly Action _action;
    private readonly SD.Bitmap? _glyph;
    private SD.Size? _ownSize;

    internal QuickActionMenuItem(string id, string text, Action action)
        : base(text)
    {
        Id = id;
        _action = action;
        if (QuickActionsMenu.IconFor(id) is { } icon)
            _glyph = SvgIcons.Get(icon, GlyphSize, ThemePalette.TextPrimary);
    }

    internal string Id { get; }

    /// <summary>How far this row's text sits from the menu's left edge.</summary>
    internal int TextLeft => PadLeft + (_glyph is null ? 0 : GlyphSize + GlyphGap);

    private string Shortcut => ShowShortcutKeys ? ShortcutKeyDisplayString ?? string.Empty : string.Empty;

    protected override void OnClick(EventArgs e)
    {
        // A submenu row has nothing to run; let the base open its drop-down.
        if (HasDropDownItems)
        {
            base.OnClick(e);
            return;
        }
        _action();
    }

    /// <summary>
    /// Every row reports the widest row's width: overriding this opts out of the
    /// drop-down's own equalizing pass, and rows of different widths would put each
    /// shortcut at its own right edge instead of in one column. Both this and
    /// <see cref="OwnSize"/> are cached — layout asks repeatedly, and measuring every
    /// sibling on every ask made opening the menu visibly slow.
    /// </summary>
    public override SD.Size GetPreferredSize(SD.Size constrainingSize)
    {
        SD.Size own = OwnSize();
        int width = Owner is QuickActionsContextMenu menu ? menu.TargetRowWidth : own.Width;
        return new SD.Size(Math.Max(width, own.Width), own.Height);
    }

    internal SD.Size OwnSize()
    {
        if (_ownSize is { } cached)
            return cached;
        SD.Size text = Measure(Text ?? string.Empty);
        int width = TextLeft + text.Width + PadRight;
        if (Shortcut.Length > 0)
            width += ShortcutGap + Measure(Shortcut).Width;
        if (HasDropDownItems)
            width += ShortcutGap + ArrowWidth;
        _ownSize = new SD.Size(width, Math.Max(MinHeight, text.Height + 5));
        return _ownSize.Value;
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        // Deliberately not base.OnPaint: that is the layout this item exists to replace.
        var g = e.Graphics;
        DrawHighlight(g);

        if (_glyph is not null)
            g.DrawImage(_glyph, PadLeft, (Height - GlyphSize) / 2, GlyphSize, GlyphSize);

        SD.Color color = Enabled ? ThemePalette.TextPrimary : ThemePalette.TextSecondary;
        var flags = WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Left;
        WF.TextRenderer.DrawText(
            g,
            Text,
            Font,
            new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - PadRight), Height),
            color,
            flags);

        if (HasDropDownItems)
        {
            DrawSubmenuArrow(g, color);
        }
        else if (Shortcut.Length > 0)
        {
            WF.TextRenderer.DrawText(
                g,
                Shortcut,
                Font,
                new SD.Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - PadRight), Height),
                ThemePalette.TextSecondary,
                WF.TextFormatFlags.NoPrefix | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Right);
        }
    }

    /// <summary>
    /// The hover/keyboard highlight: a rounded bar spanning the row, which is the full
    /// width the drop-down gives its items, so it insets evenly from both edges instead
    /// of the stock block that ran to one border and stopped short of the other.
    /// </summary>
    private void DrawHighlight(SD.Graphics g)
    {
        if (!Selected && !Pressed && !(HasDropDownItems && DropDown.Visible))
            return;
        if (Width <= 2 || Height <= 2)
            return;

        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new SD.Rectangle(0, 1, Width, Height - 2);
        using var path = GdiPaths.RoundedRect(bounds, Math.Min(6, bounds.Height / 2));
        using var fill = new SD.SolidBrush(SD.Color.FromArgb(66, 66, 70));
        g.FillPath(fill, path);
        g.SmoothingMode = previous;
    }

    /// <summary>The chevron that says this row expands. Drawn here because the row opts out
    /// of the stock layout, which would otherwise have drawn it.</summary>
    private void DrawSubmenuArrow(SD.Graphics g, SD.Color color)
    {
        const int arm = 4;
        int x = Width - PadRight - ArrowWidth / 2;
        int y = Height / 2;
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new SD.Pen(color, 1.4f)
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new SD.PointF(x - arm / 2f, y - arm),
            new SD.PointF(x + arm / 2f, y),
            new SD.PointF(x - arm / 2f, y + arm),
        });
        g.SmoothingMode = previous;
    }

    private SD.Size Measure(string value) =>
        WF.TextRenderer.MeasureText(value, Font, SD.Size.Empty, WF.TextFormatFlags.NoPrefix);
}

/// <summary>
/// The drop-down that hosts <see cref="QuickActionMenuItem"/>. It exists to size itself
/// to those rows: <see cref="WF.ToolStripDropDownMenu"/> re-derives its width from the
/// stock text/image/check metrics, so a row that draws its own glyph measures short and
/// the shortcut column gets clipped off the right edge.
/// </summary>
internal sealed class QuickActionsContextMenu : WF.ContextMenuStrip
{
    private const int CornerRadius = 8;
    private const int HorizontalGutter = 4;

    private int? _rowWidth;
    private bool _stretchingRows;

    internal QuickActionsContextMenu()
    {
        // The rows own their left inset; the drop-down must not add one of its own.
        ShowImageMargin = false;
        Renderer = new RoundedMenuRenderer();
    }

    /// <summary>
    /// Even gutters on both sides. ToolStripDropDownMenu otherwise recomputes its own
    /// padding during layout — 8 on the left, 1 on the right — which left the highlight
    /// touching one border and short of the other.
    /// </summary>
    protected override WF.Padding DefaultPadding => new(HorizontalGutter, 2, HorizontalGutter, 2);

    /// <summary>
    /// The width every row is given: the widest row's own width, or the drop-down's inner
    /// width when that turned out wider. Without the second half a row can end up narrower
    /// than the menu, and the row's highlight then stops short of the right edge — which is
    /// exactly how it looked before.
    /// </summary>
    internal int TargetRowWidth => Math.Max(RowWidth, ClientSize.Width - Padding.Horizontal);

    /// <summary>The width every row reports, measured once. Layout asks constantly.</summary>
    internal int RowWidth
    {
        get
        {
            if (_rowWidth is { } cached)
                return cached;
            int width = 0;
            foreach (WF.ToolStripItem item in Items)
            {
                if (item is QuickActionMenuItem row)
                    width = Math.Max(width, row.OwnSize().Width);
            }
            _rowWidth = width;
            return width;
        }
    }

    // The cache must not outlive the row list: layout runs while rows are still being
    // added, and a width measured then is short of the finished menu's.
    protected override void OnItemAdded(WF.ToolStripItemEventArgs e)
    {
        _rowWidth = null;
        base.OnItemAdded(e);
    }

    protected override void OnItemRemoved(WF.ToolStripItemEventArgs e)
    {
        _rowWidth = null;
        base.OnItemRemoved(e);
    }

    /// <summary>Creates the window and lays the rows out now, so the first right-click
    /// does not pay for it. Called while the card is idle.</summary>
    internal void Warm()
    {
        _ = RowWidth;
        _ = Handle;
        PerformLayout();
        foreach (WF.ToolStripItem item in Items)
        {
            if (item is WF.ToolStripMenuItem { HasDropDownItems: true, DropDown: QuickActionsContextMenu child })
                child.Warm();
        }
    }

    private int Radius => (int)Math.Round(CornerRadius * DeviceDpi / 96.0);

    public override SD.Size GetPreferredSize(SD.Size proposedSize)
    {
        SD.Size size = base.GetPreferredSize(proposedSize);
        size.Width = Math.Max(size.Width, RowWidth + Padding.Horizontal);
        return size;
    }

    protected override void OnLayoutCompleted(EventArgs e)
    {
        base.OnLayoutCompleted(e);
        PopupChrome.ApplyRegion(this, Radius);

        // The drop-down can settle wider than the rows asked for; one more pass hands them
        // that width so every highlight spans the menu. Guarded: this re-enters layout.
        if (_stretchingRows)
            return;
        int target = TargetRowWidth;
        bool shortRow = false;
        foreach (WF.ToolStripItem item in Items)
            shortRow |= item is QuickActionMenuItem && item.Width != target;
        if (!shortRow)
            return;

        _stretchingRows = true;
        try
        {
            PerformLayout();
        }
        finally
        {
            _stretchingRows = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PopupChrome.ApplyRegion(this, Radius);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        // Drawn last so the hairline follows the curve the region cut, matching the
        // capture card and the app's other popups.
        PopupChrome.DrawBorder(e.Graphics, ClientSize, Radius, SD.Color.FromArgb(70, 70, 74));
    }

    /// <summary>The dark menu look, minus its square border — the rounded one is painted
    /// over the region instead, and a rectangle would lose its corners to the clip.</summary>
    private sealed class RoundedMenuRenderer : DarkDropDown.DarkMenuRenderer
    {
        protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
        {
        }

        /// <summary>The rows draw their own rounded highlight.</summary>
        protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not QuickActionMenuItem)
                base.OnRenderMenuItemBackground(e);
        }
    }
}
