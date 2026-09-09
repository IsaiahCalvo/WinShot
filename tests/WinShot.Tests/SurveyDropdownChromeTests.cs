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
/// inverse: a white caret over a label that stayed at its resting colour. The two colours
/// themselves are WinShot's tokens, not Survey's — the mechanic is the brightening, not the
/// shade it brightens from.
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
                // Read off the live controls rather than pinned to Survey's #e8e2d4 /
                // #9da6b4: what this test guards is the RELATIONSHIP — the label brightens to
                // white on open, the chevron stays muted throughout — not the shades WinShot
                // happens to brighten from.
                var muted = ((SolidColorBrush)caret.Stroke).Color;
                var rest = ((SolidColorBrush)box.Foreground).Color;
                // The chevron sits below the resting label, which sits below the open one.
                // Which three shades those are is the palette's business — Survey's are
                // #9da6b4 / #e8e2d4 / #fff and WinShot's are its own; what has to hold is the
                // ORDER, and that the chevron never joins in.
                // Composited over the dark chrome, because WinShot's text tokens carry their
                // dimming in the ALPHA channel: #B3FFFFFF is 70 % white, which is darker on
                // this ground than an opaque #F2F2F4 even though its RGB is brighter.
                static double Lum(Color c) =>
                    c.A / 255.0 * (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
                Assert.True(Lum(muted) < Lum(rest), $"chevron {muted} is not below the label {rest}");

                // The half that matters and that a headless host can actually observe: the
                // chevron does not follow the label. Survey pins it on the icon element so
                // `currentColor` never reaches it; here that is a StaticResource rather than a
                // TemplateBinding, and this is what proves it — brighten the label all the way
                // to white and the chevron does not move.
                box.Foreground = new SolidColorBrush(Colors.White);
                host.UpdateLayout();
                Assert.Equal(muted, ((SolidColorBrush)caret.Stroke).Color);

                // And the style really does carry a brighten-on-open rule for the label.
                var openTrigger = ((Style)theme["SurveyDropdown"])
                    .Setters.OfType<Setter>()
                    .Select(st => st.Value)
                    .OfType<ControlTemplate>()
                    .SelectMany(t => t.Triggers.OfType<Trigger>())
                    .FirstOrDefault(t => t.Property == ComboBox.IsDropDownOpenProperty && Equals(t.Value, true));
                Assert.NotNull(openTrigger);
                var lit = openTrigger!.Setters.OfType<Setter>()
                    .First(st => st.Property == Control.ForegroundProperty).Value;
                Assert.True(Lum(((SolidColorBrush)lit).Color) > Lum(rest),
                    "the open label is not brighter than the resting one");

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

                // Closing puts the label back where it started, whatever "there" is in this
                // palette — the point is that the brighten is a STATE, not a one-way door.
                box.ClearValue(Control.ForegroundProperty);
                box.IsDropDownOpen = false;
                host.UpdateLayout();

                Assert.Equal(rest, ((SolidColorBrush)box.Foreground).Color);

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
