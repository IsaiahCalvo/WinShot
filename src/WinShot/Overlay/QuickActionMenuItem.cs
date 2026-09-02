using System.Drawing.Drawing2D;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// One row of the quick-actions card's context menu. Adds two things the stock item
/// has no notion of: the row's id, and an undo button parked at the right edge of the
/// row whose edit was applied last — click the glyph to revert, click anywhere else in
/// the row to run the row again.
/// </summary>
internal sealed class QuickActionMenuItem : WF.ToolStripMenuItem
{
    private const int GlyphSize = 16;
    private const int ZoneWidth = GlyphSize + 12;

    private readonly Action _action;
    private readonly Action _undo;
    private bool _pointerOverUndo;
    private bool _showUndo;

    internal QuickActionMenuItem(string id, string text, Action action, Action undo)
        : base(text)
    {
        Id = id;
        _action = action;
        _undo = undo;
        if (QuickActionsMenu.IconFor(id) is { } icon)
            Image = SvgIcons.Get(icon, GlyphSize, ThemePalette.TextPrimary);
    }

    internal string Id { get; }

    /// <summary>Whether this row is the one carrying the undo button right now.</summary>
    internal bool ShowUndo
    {
        get => _showUndo;
        set
        {
            if (_showUndo == value) return;
            _showUndo = value;
            _pointerOverUndo = false;
            // The row got wider or narrower, so the whole drop-down has to re-measure.
            Owner?.PerformLayout();
            Invalidate();
        }
    }

    public override SD.Size GetPreferredSize(SD.Size constrainingSize)
    {
        SD.Size size = base.GetPreferredSize(constrainingSize);
        if (_showUndo)
            size.Width += ZoneWidth;
        return size;
    }

    protected override void OnClick(EventArgs e)
    {
        // Keyboard activation never sets the hover flag, so Enter always runs the row.
        if (_pointerOverUndo)
        {
            _pointerOverUndo = false;
            _undo();
            return;
        }
        _action();
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetPointerOverUndo(_showUndo && UndoBounds().Contains(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetPointerOverUndo(false);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_showUndo) return;

        SD.Rectangle zone = UndoBounds();
        var g = e.Graphics;
        if (_pointerOverUndo)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = GdiPaths.RoundedRect(zone, zone.Height / 2);
            using var fill = new SD.SolidBrush(SD.Color.FromArgb(70, ThemePalette.AccentHover));
            g.FillPath(fill, path);
        }

        SD.Bitmap? glyph = SvgIcons.Get(
            QuickActionsMenu.UndoIcon,
            GlyphSize,
            _pointerOverUndo ? ThemePalette.TextPrimary : ThemePalette.TextSecondary);
        if (glyph is not null)
        {
            g.DrawImage(
                glyph,
                zone.X + (zone.Width - GlyphSize) / 2,
                zone.Y + (zone.Height - GlyphSize) / 2,
                GlyphSize,
                GlyphSize);
        }
    }

    /// <summary>The undo hit zone, in the row's own coordinates.</summary>
    private SD.Rectangle UndoBounds()
    {
        int height = Math.Min(Height - 2, GlyphSize + 6);
        return new SD.Rectangle(
            Math.Max(0, Width - ZoneWidth - 2),
            Math.Max(0, (Height - height) / 2),
            ZoneWidth,
            Math.Max(1, height));
    }

    private void SetPointerOverUndo(bool value)
    {
        if (_pointerOverUndo == value) return;
        _pointerOverUndo = value;
        Invalidate();
    }
}
