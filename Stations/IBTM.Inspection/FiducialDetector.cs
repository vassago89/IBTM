using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;
using OpenCvSharp;

namespace IBTM.Inspection;

public sealed record FiducialDetection(Point2D Center, double RadiusPixels, double ShapeError, Point2D[] Contour);

public sealed class FiducialDetectionException : InvalidOperationException
{
    public FiducialDetectionException(string message) : base(message)
    {
    }
}

public static class FiducialDetector
{
    public static PcbAlignment GetAlignment(FiducialObservation first, FiducialObservation second)
    {
        if (double.Hypot(second.Reference.X - first.Reference.X, second.Reference.Y - first.Reference.Y) < 0.001
            || double.Hypot(second.Measured.X - first.Measured.X, second.Measured.Y - first.Measured.Y) < 0.001)
            throw new FiducialDetectionException(UiText.Get("Fiducials must be at two different positions"));
        return new(DateTimeOffset.Now, first, second);
    }

    public static double? GetReferenceRadiusMm(FiducialMark mark, double? mmPerPixel)
    {
        if (mark.ReferenceRadiusMm is { } radius)
            return radius;
        // Older recipes used the area of the registered contour as the size reference.
        if (mark.Contour.Length < 5 || mmPerPixel is not > 0)
            return null;
        var twiceArea = 0d;
        var previous = mark.Contour[^1];
        foreach (var point in mark.Contour)
        {
            twiceArea += previous.X * point.Y - point.X * previous.Y;
            previous = point;
        }
        var area = Math.Abs(twiceArea) / 2;
        return double.IsFinite(area) && area >= 4 ? Math.Sqrt(area / Math.PI) * mmPerPixel : null;
    }

    public static FiducialDetection Detect(ImageFrame frame, FiducialMark mark, double? mmPerPixel, bool registering = false)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < (long)frame.Width * 3
            || frame.Pixels.LongLength < (long)frame.Stride * frame.Height)
            throw new ArgumentException(UiText.Get("Invalid image buffer."), nameof(frame));
        if (!registering && (mark.Contour.Length < 5 || mark.ImageWidth != frame.Width || mark.ImageHeight != frame.Height))
            throw new InvalidOperationException(UiText.Get("Register the fiducial contour"));
        if (mmPerPixel is not { } resolution || !double.IsFinite(resolution) || resolution <= 0)
            throw new InvalidOperationException(UiText.Get("Set camera resolution"));
        var referenceRadius = GetReferenceRadiusMm(mark, resolution)
            ?? throw new InvalidOperationException(UiText.Get("Enter a positive fiducial radius"));

        var region = PixelRegion.CenteredSquare(frame.Width, frame.Height, mark.SearchSizePixels);
        var search = new Rect(region.X, region.Y, region.Width, region.Height);
        using var image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3, frame.Pixels, frame.Stride);
        using var cropped = new Mat(image, search);
        using var binary = Binarize(cropped, mark.Threshold, mark.WhiteMark);
        var contours = binary.FindContoursAsArray(RetrievalModes.External, ContourApproximationModes.ApproxNone,
            new Point(search.X, search.Y));
        using var reference = registering ? null
            : InputArray.Create(mark.Contour.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray());
        if (reference is not null && !(Math.Abs(Cv2.ContourArea(reference)) >= 4))
            throw new InvalidOperationException(UiText.Get("Register the fiducial contour"));
        var accepted = new List<FiducialDetection>();
        foreach (var contour in contours)
        {
            var bounds = Cv2.BoundingRect(contour);
            if (contour.Length < 5)
                continue;
            var area = Cv2.ContourArea(contour);
            if (area < 4
                || bounds.Left <= search.Left || bounds.Top <= search.Top
                || bounds.Right >= search.Right || bounds.Bottom >= search.Bottom)
                continue;
            var radiusPixels = Math.Sqrt(area / Math.PI);
            var radiusMm = radiusPixels * resolution;
            if (Math.Abs(radiusMm - referenceRadius) > referenceRadius * mark.SizeTolerancePercent / 100)
                continue;
            var perimeter = Cv2.ArcLength(contour, true);
            if (4 * Math.PI * area / (perimeter * perimeter) < 0.8)
                continue;
            var error = 0d;
            if (!registering)
            {
                using var candidate = InputArray.Create(contour);
                error = Cv2.MatchShapes(reference!, candidate, ShapeMatchModes.I3);
                if (!double.IsFinite(error) || error > mark.MaximumShapeError)
                    continue;
            }

            var points = contour.Select(point => new Point2D(point.X, point.Y)).ToArray();
            accepted.Add(new(FitCircle(points), radiusPixels, error, points));
        }
        return accepted.Count switch
        {
            1 => accepted[0],
            0 => throw new FiducialDetectionException(UiText.Get("Fiducial not found")),
            _ => throw new FiducialDetectionException(UiText.Get("Multiple fiducials found in the search region")),
        };
    }

    public static Mat Binarize(Mat bgr, int threshold, bool whiteMark)
    {
        using var gray = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
        return gray.Threshold(threshold, 255, whiteMark ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
    }

    public static Point2D FitCircle(Point2D[] points)
    {
        using var input = InputArray.Create(points.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray());
        // Fit the whole outline, rather than its extremal pixels or bounding rectangle.
        var origin = Cv2.Moments(input);
        var ox = origin.M10 / origin.M00;
        var oy = origin.M01 / origin.M00;
        using (var a = new Mat(points.Length, 3, MatType.CV_64FC1))
        using (var b = new Mat(points.Length, 1, MatType.CV_64FC1))
        using (var solution = new Mat())
        {
            for (var index = 0; index < points.Length; index++)
            {
                var x = points[index].X - ox;
                var y = points[index].Y - oy;
                a.Set(index, 0, 2 * x);
                a.Set(index, 1, 2 * y);
                a.Set(index, 2, 1d);
                b.Set(index, 0, x * x + y * y);
            }
            if (!Cv2.Solve(a, b, solution, DecompTypes.SVD))
                throw new FiducialDetectionException(UiText.Get("Circle fit failed"));
            return new(ox + solution.At<double>(0), oy + solution.At<double>(1));
        }
    }
}
