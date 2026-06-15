using System.Windows;
using System.Windows.Controls;

namespace WinShot.Editor;

/// <summary>
/// Width/height resize dialog with aspect-ratio lock and a percent shortcut.
/// Editing width or height keeps the other in sync while the lock is on; typing
/// a percentage (or clicking a quick-percent button) recomputes both from the
/// original size. Returns the chosen size via <see cref="ResultWidth"/> /
/// <see cref="ResultHeight"/> when ShowDialog() returns true.
/// </summary>
public partial class ResizeDialog : Window
{
    private const int MaxDimension = 20000;

    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private bool _updating;

    public int ResultWidth { get; private set; }
    public int ResultHeight { get; private set; }

    public ResizeDialog(int currentWidth, int currentHeight)
    {
        InitializeComponent();
        _originalWidth = Math.Max(1, currentWidth);
        _originalHeight = Math.Max(1, currentHeight);
        ResultWidth = _originalWidth;
        ResultHeight = _originalHeight;

        _updating = true;
        WidthBox.Text = _originalWidth.ToString();
        HeightBox.Text = _originalHeight.ToString();
        PercentBox.Text = "100";
        _updating = false;
        UpdateInfo();

        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    private void OnWidthChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!int.TryParse(WidthBox.Text, out int w) || w < 1)
        {
            UpdateInfo();
            return;
        }
        _updating = true;
        if (LockRatio.IsChecked == true)
            HeightBox.Text = Math.Max(1,
                (int)Math.Round(w * _originalHeight / (double)_originalWidth)).ToString();
        PercentBox.Text = Math.Round(w * 100.0 / _originalWidth).ToString();
        _updating = false;
        UpdateInfo();
    }

    private void OnHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!int.TryParse(HeightBox.Text, out int h) || h < 1)
        {
            UpdateInfo();
            return;
        }
        _updating = true;
        if (LockRatio.IsChecked == true)
        {
            WidthBox.Text = Math.Max(1,
                (int)Math.Round(h * _originalWidth / (double)_originalHeight)).ToString();
            PercentBox.Text = Math.Round(h * 100.0 / _originalHeight).ToString();
        }
        _updating = false;
        UpdateInfo();
    }

    private void OnPercentChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!double.TryParse(PercentBox.Text, out double pct) || pct <= 0)
        {
            UpdateInfo();
            return;
        }
        _updating = true;
        WidthBox.Text = Math.Max(1, (int)Math.Round(_originalWidth * pct / 100.0)).ToString();
        HeightBox.Text = Math.Max(1, (int)Math.Round(_originalHeight * pct / 100.0)).ToString();
        _updating = false;
        UpdateInfo();
    }

    private void OnQuickPercent(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string pct)
            PercentBox.Text = pct; // TextChanged recomputes width/height
    }

    private (int Width, int Height)? Validate()
    {
        if (!int.TryParse(WidthBox.Text, out int w) || !int.TryParse(HeightBox.Text, out int h))
            return null;
        if (w < 1 || h < 1 || w > MaxDimension || h > MaxDimension)
            return null;
        return (w, h);
    }

    private void UpdateInfo()
    {
        InfoText.Text = Validate() is { } size
            ? $"{_originalWidth} × {_originalHeight} px  →  {size.Width} × {size.Height} px"
            : $"Enter a size between 1 and {MaxDimension} px.";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Validate() is not { } size)
        {
            UpdateInfo();
            return;
        }
        ResultWidth = size.Width;
        ResultHeight = size.Height;
        DialogResult = true;
    }
}
