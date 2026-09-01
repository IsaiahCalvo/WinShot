using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Editor;
using Xunit;
using SD = System.Drawing;

namespace WinShot.Tests;

public class AnnotationTransformTests
{
    /// <summary>Runs a body on an STA thread — WPF elements cannot be built on the xunit thread.</summary>
    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static System.Windows.Shapes.Rectangle MeasuredRect(double w = 120, double h = 80)
    {
        var rect = new System.Windows.Shapes.Rectangle { Width = w, Height = h };
        rect.Measure(new Size(w, h));
        rect.Arrange(new Rect(0, 0, w, h));
        return rect;
    }

    [Fact]
    public void UnrotatedElement_KeepsAPlainTranslateTransform()
    {
        OnSta(() =>
        {
            var rect = MeasuredRect();
            AnnotationTransform.Apply(rect, new Vector(10, 20), 0);

            // Nothing about the old on-disk / runtime shape changes when there is no rotation.
            var t = Assert.IsType<TranslateTransform>(rect.RenderTransform);
            Assert.Equal(10, t.X);
            Assert.Equal(20, t.Y);
            Assert.Equal(0, AnnotationTransform.AngleOf(rect));
            Assert.Equal(new Vector(10, 20), AnnotationTransform.OffsetOf(rect));
        });
    }

    [Fact]
    public void RotatedElement_CarriesBothOffsetAndAngle()
    {
        OnSta(() =>
        {
            var rect = MeasuredRect();
            AnnotationTransform.Apply(rect, new Vector(10, 20), 30);

            Assert.IsType<TransformGroup>(rect.RenderTransform);
            Assert.Equal(30, AnnotationTransform.AngleOf(rect));
            Assert.Equal(new Vector(10, 20), AnnotationTransform.OffsetOf(rect));
        });
    }

    [Fact]
    public void RotationPivotIsTheElementsOwnCentre()
    {
        OnSta(() =>
        {
            var rect = MeasuredRect(120, 80);
            AnnotationTransform.SetAngle(rect, 45);
            Assert.Equal(new Point(60, 40), AnnotationTransform.PivotOf(rect));
        });
    }

    [Fact]
    public void SetOffset_PreservesAnExistingRotation()
    {
        OnSta(() =>
        {
            var rect = MeasuredRect();
            AnnotationTransform.SetAngle(rect, 30);
            AnnotationTransform.SetOffset(rect, new Vector(5, 6));

            // Moving a rotated mark must not straighten it.
            Assert.Equal(30, AnnotationTransform.AngleOf(rect));
            Assert.Equal(new Vector(5, 6), AnnotationTransform.OffsetOf(rect));
        });
    }

    [Fact]
    public void SettingAngleBackToZero_CollapsesToATranslate()
    {
        OnSta(() =>
        {
            var rect = MeasuredRect();
            AnnotationTransform.Apply(rect, new Vector(4, 4), 30);
            AnnotationTransform.SetAngle(rect, 0);

            Assert.IsType<TranslateTransform>(rect.RenderTransform);
            Assert.Equal(new Vector(4, 4), AnnotationTransform.OffsetOf(rect));
        });
    }

    [Fact]
    public void Angle_SurvivesAProjectRoundTrip()
    {
        OnSta(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"winshot-rot-{Guid.NewGuid():N}.winshot");
            try
            {
                using var bitmap = new SD.Bitmap(40, 30);
                var doc = new ProjectDocument();
                doc.Annotations.Add(new AnnotationData
                {
                    Type = AnnotationData.TypeRectangle,
                    Rect = new[] { 10d, 12d, 60d, 40d },
                    Color = "#FFFF453A",
                    Thickness = 4,
                    Tx = 7,
                    Ty = 9,
                    Angle = -37.5,
                });

                ProjectSerializer.Save(path, bitmap, doc, Array.Empty<BitmapSource>());
                var (source, loaded, _) = ProjectSerializer.Load(path);
                source.Dispose();

                var a = Assert.Single(loaded.Annotations);
                Assert.Equal(-37.5, a.Angle, 6);
                Assert.Equal(7, a.Tx, 6);
                Assert.Equal(9, a.Ty, 6);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    [Fact]
    public void ProjectsWrittenBeforeRotation_ReadBackUnrotated()
    {
        OnSta(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"winshot-old-{Guid.NewGuid():N}.winshot");
            try
            {
                using var bitmap = new SD.Bitmap(40, 30);
                var doc = new ProjectDocument();
                // No Angle set at all — exactly what an older file carries.
                doc.Annotations.Add(new AnnotationData
                {
                    Type = AnnotationData.TypeRectangle,
                    Rect = new[] { 0d, 0d, 20d, 20d },
                    Color = "#FFFF453A",
                    Thickness = 2,
                });

                ProjectSerializer.Save(path, bitmap, doc, Array.Empty<BitmapSource>());
                var (source, loaded, _) = ProjectSerializer.Load(path);
                source.Dispose();

                Assert.Equal(0, Assert.Single(loaded.Annotations).Angle);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }
}
