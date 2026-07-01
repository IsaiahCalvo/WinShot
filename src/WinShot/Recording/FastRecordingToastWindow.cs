using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinShot.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WinShot.Recording;

public sealed class FastRecordingToastWindow : WF.Form
{
    private static readonly SD.Color Back = SD.Color.FromArgb(43, 43, 43);
    private static readonly SD.Color ButtonBack = SD.Color.FromArgb(58, 58, 58);
    private static readonly SD.Color ButtonHot = SD.Color.FromArgb(79, 79, 79);
    private static readonly SD.Color Accent = ThemePalette.Accent;
    private static readonly SD.Color AccentHot = ThemePalette.AccentHover;
    private static readonly SD.Color Border = SD.Color.FromArgb(54, 255, 255, 255);
    private static readonly SD.Color TextColor = SD.Color.White;
    private static readonly SD.Color AccentText = ThemePalette.AccentHover;

    private readonly string _filePath;
    private readonly Action? _onEdit;
    private readonly WF.Timer _dismissTimer = new() { Interval = 8000 };

    public FastRecordingToastWindow(string filePath, Action? onEdit)
    {
        _filePath = filePath;
        _onEdit = onEdit;

        AutoScaleMode = WF.AutoScaleMode.None;
        BackColor = Back;
        ClientSize = new SD.Size(328, 118);
        FormBorderStyle = WF.FormBorderStyle.None;
        KeyPreview = true;
        Opacity = 0.96;
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

        var title = Label("Recording saved", 14, 14, 220, 12, AccentText, bold: true);
        Controls.Add(title);

        var close = Button("x", 286, 10, 26, 24, ButtonBack, ButtonHot);
        close.Click += (_, _) => Close();
        Controls.Add(close);

        string name = Path.GetFileName(filePath);
        var fileName = Label(name, 14, 39, 300, 13, TextColor);
        fileName.AutoEllipsis = true;
        fileName.UseMnemonic = false;
        fileName.TextAlign = SD.ContentAlignment.MiddleLeft;
        var tip = new WF.ToolTip { InitialDelay = 300, ReshowDelay = 100 };
        tip.SetToolTip(fileName, filePath);
        Disposed += (_, _) => tip.Dispose();
        Controls.Add(fileName);

        int x = onEdit is null ? 174 : 110;
        var open = Button("Open", x, 78, 58, 26, ButtonBack, ButtonHot);
        open.Click += (_, _) => { OpenFile(); Close(); };
        Controls.Add(open);
        x += 64;

        var reveal = Button("Reveal", x, 78, 64, 26, ButtonBack, ButtonHot);
        reveal.Click += (_, _) => { RevealFile(); Close(); };
        Controls.Add(reveal);
        x += 70;

        if (onEdit is not null)
        {
            var edit = Button("Edit...", x, 78, 64, 26, Accent, AccentHot);
            edit.Click += (_, _) => { EditFile(); Close(); };
            Controls.Add(edit);
        }

        MouseEnter += (_, _) => _dismissTimer.Stop();
        MouseLeave += (_, _) => _dismissTimer.Start();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == WF.Keys.Escape)
                Close();
        };
        _dismissTimer.Tick += (_, _) => Close();
    }

    protected override bool ShowWithoutActivation => true;

    public static void Prewarm()
    {
        try
        {
            using var toast = new FastRecordingToastWindow(
                Path.Combine(Path.GetTempPath(), "winshot-prewarm.mp4"),
                onEdit: null)
            {
                Opacity = 0,
                Location = new SD.Point(-32000, -32000),
            };
            toast.Show();
            WF.Application.DoEvents();
            using var render = new SD.Bitmap(toast.Width, toast.Height);
            toast.DrawToBitmap(render, new SD.Rectangle(0, 0, render.Width, render.Height));
            toast.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Fast recording toast prewarm failed", ex);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateWindowRegion();
        PositionBottomRight();
        _dismissTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismissTimer.Stop();
        _dismissTimer.Dispose();
        base.OnClosed(e);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new SD.Pen(Border, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void PositionBottomRight()
    {
        SD.Rectangle area = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
        Location = new SD.Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private void OpenFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open recording {_filePath}", ex);
        }
    }

    private void RevealFile()
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to reveal recording {_filePath}", ex);
        }
    }

    private void EditFile()
    {
        try
        {
            _onEdit?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open video editor for {_filePath}", ex);
        }
    }

    private void UpdateWindowRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        IntPtr regionHandle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 20, 20);
        Region = SD.Region.FromHrgn(regionHandle);
        DeleteObject(regionHandle);
    }

    private static WF.Label Label(string text, int x, int y, int width, float size, SD.Color color, bool bold = false) =>
        new()
        {
            AutoSize = false,
            BackColor = SD.Color.Transparent,
            Font = new SD.Font("Segoe UI", size, bold ? SD.FontStyle.Bold : SD.FontStyle.Regular),
            ForeColor = color,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, 22),
            Text = text,
        };

    private static WF.Button Button(
        string text,
        int x,
        int y,
        int width,
        int height,
        SD.Color backColor,
        SD.Color hotColor)
    {
        var button = new WF.Button
        {
            AutoSize = false,
            BackColor = backColor,
            Cursor = WF.Cursors.Hand,
            FlatStyle = WF.FlatStyle.Flat,
            ForeColor = TextColor,
            Location = new SD.Point(x, y),
            Size = new SD.Size(width, height),
            Text = text,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = hotColor;
        return button;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
