using WinShot.Core;
using WinShot.Recording;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Overlay;

/// <summary>
/// Width/height prompt for the thumbnail card's Resize… row. Aspect stays locked
/// unless the user unlocks it, so typing one field fills in the other.
/// </summary>
public sealed class ResizeImageDialog : WF.Form
{
    private static readonly SD.Color Back = ThemePalette.ToolbarBg;
    private static readonly SD.Color FieldBack = ThemePalette.SurfaceAlt;

    private const int DialogWidth = 246;
    private const int PadX = 18;
    private const int FieldX = 96;

    private readonly SD.Size _source;
    private readonly DarkNumberBox _width;
    private readonly DarkNumberBox _height;
    private readonly DarkButton _lock;
    private readonly double _scale;
    private bool _linked = true;
    private bool _syncing;

    public ResizeImageDialog(SD.Size source)
    {
        _source = source;
        _scale = RecordingMonitorDpi.ScaleFor(WF.Screen.FromPoint(WF.Cursor.Position).Bounds);

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.CenterScreen;
        TopMost = true;
        Font = ThemePalette.UiFont(9f);
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        var title = Label("Resize image", PadX, 200, bold: true, size: 11);
        title.Top = S(16);
        var original = Label($"{source.Width} × {source.Height} px", PadX, 200, color: ThemePalette.TextSecondary, size: 8);
        original.Top = S(38);

        var widthLabel = Label("Width", PadX, 70);
        widthLabel.Top = S(70);
        _width = NumberBox(source.Width.ToString());
        _width.Top = S(64);

        var heightLabel = Label("Height", PadX, 70);
        heightLabel.Top = S(108);
        _height = NumberBox(source.Height.ToString());
        _height.Top = S(102);

        _width.TextChanged += (_, _) => SyncFrom(_width, isWidth: true);
        _height.TextChanged += (_, _) => SyncFrom(_height, isWidth: false);

        _lock = ActionButton("Aspect locked", ThemePalette.Accent);
        _lock.Size = new SD.Size(S(DialogWidth - PadX * 2), S(28));
        _lock.Location = new SD.Point(S(PadX), S(142));
        _lock.Click += (_, _) =>
        {
            _linked = !_linked;
            _lock.FillColor = _linked ? ThemePalette.Accent : FieldBack;
            _lock.Text = _linked ? "Aspect locked" : "Aspect free";
            _lock.Invalidate();
            if (_linked)
                SyncFrom(_width, isWidth: true);
        };

        var resize = ActionButton("Resize", ThemePalette.Accent);
        resize.Location = new SD.Point(S(DialogWidth - PadX - 88), S(186));
        resize.DialogResult = WF.DialogResult.OK;
        var cancel = ActionButton("Cancel", FieldBack);
        cancel.Location = new SD.Point(S(DialogWidth - PadX - 88 - 94), S(186));
        cancel.DialogResult = WF.DialogResult.Cancel;
        AcceptButton = resize;
        CancelButton = cancel;

        ClientSize = new SD.Size(S(DialogWidth), S(230));
        Controls.AddRange([title, original, widthLabel, _width, heightLabel, _height, _lock, cancel, resize]);
        Shown += (_, _) => UpdateWindowRegion();
    }

    /// <summary>The chosen size, or null when the fields do not parse to a usable one.</summary>
    public SD.Size? Result =>
        int.TryParse(_width.Text, out int w) && int.TryParse(_height.Text, out int h) &&
        w is > 0 and <= 20000 && h is > 0 and <= 20000
            ? new SD.Size(w, h)
            : null;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    private void SyncFrom(DarkNumberBox source, bool isWidth)
    {
        if (!_linked || _syncing) return;
        if (!int.TryParse(source.Text, out int value) || value <= 0) return;

        _syncing = true;
        try
        {
            if (isWidth)
                _height.Text = QuickActionsMenu.AspectHeight(_source, value).ToString();
            else
                _width.Text = QuickActionsMenu.AspectWidth(_source, value).ToString();
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        PopupChrome.DrawBorder(e.Graphics, ClientSize, S(10));
    }

    private void UpdateWindowRegion()
    {
        if (Width > 0 && Height > 0)
            PopupChrome.ApplyRegion(this, S(10));
    }

    private WF.Label Label(string text, int x, int width, bool bold = false, float size = 9, SD.Color? color = null) =>
        new()
        {
            AutoSize = false,
            BackColor = Back,
            Font = ThemePalette.UiFont(size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color ?? ThemePalette.TextPrimary,
            Location = new SD.Point(S(x), 0),
            Size = new SD.Size(S(width), S(22)),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };

    private DarkNumberBox NumberBox(string text) =>
        new()
        {
            Scale = _scale,
            BackColor = Back,
            Location = new SD.Point(S(FieldX), 0),
            Size = new SD.Size(S(DialogWidth - FieldX - PadX), S(28)),
            Text = text,
        };

    private DarkButton ActionButton(string text, SD.Color fillColor) =>
        new()
        {
            Scale = _scale,
            BackColor = Back,
            FillColor = fillColor,
            Size = new SD.Size(S(88), S(32)),
            Text = text,
        };
}
