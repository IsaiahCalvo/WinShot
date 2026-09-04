using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

using Xunit;

namespace WinShot.Tests;

/// <summary>
/// AnnotationDropdown.css:25-29 brightens the TRIGGER LABEL to #fff on hover/open, while :46-49
/// pins `.annotation-dropdown__chevron` to #9da6b4 on the icon element itself — so the chevron's
/// `currentColor` (AnnotationDropdown.jsx:96) never inherits that white. WinShot used to do the
/// inverse: a white caret over a label that stayed #E8E2D4.
/// </summary>
public class SurveyDropdownChromeTests
{
    [Fact]
    public void OpenDropdown_BrightensTheLabelAndLeavesTheChevronMuted()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // No Application here on purpose: the process-wide singleton belongs to
                // ThemedWindowTests, and taking it first would make that test throw. The pack URI
                // names the WinShot assembly outright, so it only needs a ResourceAssembly to exist.
                Application.ResourceAssembly ??= typeof(WinShot.Core.ThemeResources).Assembly;
                var theme = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WinShot;component/Theme/Theme.xaml", UriKind.Absolute),
                };

                var box = new ComboBox
                {
                    Style = (Style)theme["SurveyDropdown"],
                    ItemsSource = new[] { "Solid", "Dashed" },
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var host = new Window { Width = 200, Height = 80, Content = box, ShowInTaskbar = false };
                host.Show();
                box.ApplyTemplate();

                // The caret lives in the ToggleButton's OWN template, not the ComboBox's.
                var toggle = (ToggleButton)box.Template.FindName("Toggle", box)!;
                toggle.ApplyTemplate();
                var caret = (ShapePath)toggle.Template.FindName("Caret", toggle)!;
                var muted = Color.FromRgb(0x9D, 0xA6, 0xB4);

                Assert.Equal(Color.FromRgb(0xE8, 0xE2, 0xD4), ((SolidColorBrush)box.Foreground).Color);
                Assert.Equal(muted, ((SolidColorBrush)caret.Stroke).Color);

                box.IsDropDownOpen = true;
                host.UpdateLayout();

                Assert.Equal(Colors.White, ((SolidColorBrush)box.Foreground).Color);
                Assert.Equal(muted, ((SolidColorBrush)caret.Stroke).Color);

                if (Environment.GetEnvironmentVariable("WINSHOT_RENDER_OUTPUT") is string dir &&
                    !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    var png = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)box.ActualWidth * 4, (int)box.ActualHeight * 4, 384, 384, PixelFormats.Pbgra32);
                    png.Render(box);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(png));
                    using var fs = File.Create(Path.Combine(dir, "dropdown-open-label.png"));
                    enc.Save(fs);
                }

                box.IsDropDownOpen = false;
                host.UpdateLayout();

                Assert.Equal(Color.FromRgb(0xE8, 0xE2, 0xD4), ((SolidColorBrush)box.Foreground).Color);

                host.Close();

            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
    }
}
