using System.Windows;
using System.Windows.Controls;
using WinShot.Core;

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
        DarkTitleBar.Apply(this);
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
        var layout = ResizeLayout.FromWidth(
            _originalWidth,
            _originalHeight,
            CurrentOrOriginalHeight(),
            w,
            LockRatio.IsChecked == true);
        WidthBox.Text = layout.Width.ToString();
        HeightBox.Text = layout.Height.ToString();
        PercentBox.Text = layout.Percent.ToString();
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
        var layout = ResizeLayout.FromHeight(
            _originalWidth,
            _originalHeight,
            CurrentOrOriginalWidth(),
            h,
            LockRatio.IsChecked == true);
        WidthBox.Text = layout.Width.ToString();
        HeightBox.Text = layout.Height.ToString();
        PercentBox.Text = layout.Percent.ToString();
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
        var layout = ResizeLayout.FromPercent(_originalWidth, _originalHeight, pct);
        WidthBox.Text = layout.Width.ToString();
        HeightBox.Text = layout.Height.ToString();
        PercentBox.Text = layout.Percent.ToString();
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
        if (!ResizeLayout.IsValid(w, h))
            return null;
        return (w, h);
    }

    private void UpdateInfo()
    {
        InfoText.Text = Validate() is { } size
            ? $"{_originalWidth} × {_originalHeight} px  →  {size.Width} × {size.Height} px"
            : $"Enter a size between 1 and {ResizeLayout.MaxDimension} px.";
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

    private int CurrentOrOriginalWidth() =>
        int.TryParse(WidthBox.Text, out int width) && width > 0 ? width : _originalWidth;

    private int CurrentOrOriginalHeight() =>
        int.TryParse(HeightBox.Text, out int height) && height > 0 ? height : _originalHeight;
}
