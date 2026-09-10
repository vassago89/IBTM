using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace IBTM.Inspection.Training;

internal static class BoltPolygon
{
    internal static StreamGeometry Geometry(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: closed);
            context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    internal static byte[] Mask(IReadOnlyList<Point> points)
    {
        var size = IBoltRecessSegmenter.InputSize;
        var mask = new byte[size * size];
        if (points.Count < 3)
            return mask;
        var polygon = Geometry(points, closed: true);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (polygon.FillContains(new Point(x + 0.5, y + 0.5)))
                    mask[y * size + x] = byte.MaxValue;
        return mask;
    }
}
