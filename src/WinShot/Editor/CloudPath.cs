using System.Windows;
using System.Windows.Media;

namespace WinShot.Editor;

/// <summary>
/// Scalloped "revision cloud" border for rectangles. Survey stores this as a
/// rectangle with <c>lineStyle: cloud</c>; WinShot renders it as a Path.
/// </summary>
internal static class CloudPath
{
    public static PathGeometry ForRectangle(Rect bounds, double intensity = 2)
    {
        double bump = Math.Clamp(intensity * 4, 6, 18);
        var geometry = new PathGeometry();
        var figure = new PathFigure { IsClosed = true, IsFilled = true };

        Point[] corners =
        {
            bounds.TopLeft,
            bounds.TopRight,
            bounds.BottomRight,
            bounds.BottomLeft,
        };
        Vector[] outwards =
        {
            new(0, -1),
            new(1, 0),
            new(0, 1),
            new(-1, 0),
        };

        bool started = false;
        for (int edge = 0; edge < 4; edge++)
        {
            Point a = corners[edge];
            Point b = corners[(edge + 1) % 4];
            Vector along = b - a;
            double length = along.Length;
            if (length < 1)
                continue;
            along.Normalize();
            Vector outDir = outwards[edge];
            int bumps = Math.Max(2, (int)Math.Floor(length / (bump * 1.6)));
            double step = length / bumps;
            for (int i = 0; i < bumps; i++)
            {
                Point start = a + along * (step * i);
                Point end = a + along * (step * (i + 1));
                Point mid = a + along * (step * (i + 0.5)) + outDir * bump;
                if (!started)
                {
                    figure.StartPoint = start;
                    started = true;
                }
                figure.Segments.Add(new QuadraticBezierSegment(mid, end, isStroked: true));
            }
        }

        if (!started)
        {
            figure.StartPoint = bounds.TopLeft;
            figure.Segments.Add(new LineSegment(bounds.TopRight, true));
            figure.Segments.Add(new LineSegment(bounds.BottomRight, true));
            figure.Segments.Add(new LineSegment(bounds.BottomLeft, true));
        }

        geometry.Figures.Add(figure);
        return geometry;
    }
}
