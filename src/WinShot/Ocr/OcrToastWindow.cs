using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Ocr;

/// <summary>
/// A small themed in-place confirmation HUD shown near the OCR selection after text is
/// copied — CleanShot-style instant feedback instead of the slow, Focus-Assist-throttled
/// Windows tray balloon. Auto-dismisses; shows an "Open" action for a decoded URL.
/// </summary>
public sealed class OcrToastWindow : WF.Form
{
    private static readonly SD.Color Back = HudChrome.Fill;
    private readonly WF.Timer _dismiss = new();
    private readonly SD.Point? _anchor;

    public OcrToastWindow(string title, string preview, SD.Point? anchorScreen, Action? onOpen)
    {
        _anchor = anchorScreen;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.97;
        Padding = new WF.Padding(14);
        ShowInTaskbar = false;
        StartPosition = WF.FormStartPosition.Manual;
        TopMost = true;
        SetStyle(
            WF.ControlStyles.AllPaintingInWmPaint |
            WF.ControlStyles.OptimizedDoubleBuffer |
            WF.ControlStyles.ResizeRedraw |
            WF.ControlStyles.UserPaint,
            true);

        const int width = 300;
        bool hasOpen = onOpen is not null;
        int height = hasOpen ? 96 : 70;
        ClientSize = new SD.Size(width, height);

        var titleLabel = Label(title, 40, 14, width - 54, 11f, ThemePalette.TextPrimary, bold: true);
        Controls.Add(titleLabel);

        var previewLabel = Label(Trim(preview), 14, 38, width - 28, 9.5f, ThemePalette.TextSecondary);
        previewLabel.AutoEllipsis = true;
        previewLabel.UseMnemonic = false;
        Controls.Add(previewLabel);

        if (onOpen is not null)
        {
            var open = Button("Open link", width - 14 - 96, 60, 96, 26, ThemePalette.ActionBg, ThemePalette.ActionBgHover);
            open.Click += (_, _) => { try { onOpen(); } catch (Exception ex) { Log.Error("OCR toast open failed", ex); } Close(); };
            Controls.Add(open);
        }

        _dismiss.Interval = hasOpen ? 4500 : 1700;
        _dismiss.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _dismiss.Stop();
        MouseLeave += (_, _) => _dismiss.Start();
        foreach (WF.Control c in Controls)
        {
            c.MouseEnter += (_, _) => _dismiss.Stop();
            c.MouseLeave += (_, _) => _dismiss.Start();
        }
        KeyDown += (_, e) => { if (e.KeyCode == WF.Keys.Escape) Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateRegion();
        Position();
        _dismiss.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismiss.Stop();
        _dismiss.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        HudChrome.Paint(e.Graphics, new SD.Rectangle(0, 0, Width, Height), 14);

        // Leading state dot: blue-tint circle with a light-blue check (design 1g).
        var circle = new SD.Rectangle(14, 14, 18, 18);
        using (var tint = new SD.SolidBrush(SD.Color.FromArgb(0x47, 0x2E, 0x6F, 0xC1)))
            e.Graphics.FillEllipse(tint, circle);
        using var check = new SD.Pen(SD.Color.FromArgb(0x8F, 0xBC, 0xE8), 1.8f)
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        e.Graphics.DrawLines(check, new[]
        {
            new SD.PointF(circle.Left + 4.5f, circle.Top + 9.5f),
            new SD.PointF(circle.Left + 7.5f, circle.Top + 12.5f),
            new SD.PointF(circle.Left + 13.5f, circle.Top + 5.5f),
        });
    }

    private void Position()
    {
        SD.Rectangle area = WF.Screen.FromPoint(_anchor ?? WF.Cursor.Position).WorkingArea;
        int x, y;
        if (_anchor is SD.Point a)
        {
            // Centered above the selection; flip below if there isn't room.
            x = a.X - Width / 2;
            y = a.Y - Height - 16;
            x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
            if (y < area.Top + 8) y = a.Y + 16;
            y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        }
        else
        {
            x = area.Right - Width - 16;
            y = area.Bottom - Height - 16;
        }
        Location = new SD.Point(x, y);
    }

    private static string Trim(string text)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 90 ? text[..90] + "…" : text;
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        IntPtr rgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 28, 28);
        Region = SD.Region.FromHrgn(rgn);
        DeleteObject(rgn);
    }

    private static WF.Label Label(string text, int x, int y, int width, float size, SD.Color color, bool bold = false) =>
        new()
        {
            AutoSize = false,
            BackColor = SD.Color.Transparent,
            Font = bold ? ThemePalette.UiFontSemiBold(size) : ThemePalette.UiFont(size),
            ForeColor = color,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, 22),
            Text = text,
            TextAlign = SD.ContentAlignment.MiddleLeft,
        };

    private static WF.Button Button(string text, int x, int y, int width, int height, SD.Color back, SD.Color hot)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = back,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = SD.Color.White,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, height),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hot;
        button.ForeColor = ThemePalette.ActionText;
        button.Font = ThemePalette.UiFontSemiBold(9f);
        void ApplyRegion()
        {
            IntPtr rgn = CreateRoundRectRgn(0, 0, button.Width + 1, button.Height + 1, button.Height, button.Height);
            button.Region = SD.Region.FromHrgn(rgn);
            DeleteObject(rgn);
        }
        button.HandleCreated += (_, _) => ApplyRegion();
        return button;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
