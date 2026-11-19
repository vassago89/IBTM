using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using IBTM.Core;
using OpenCvSharp;

namespace IBTM.Inspection;

public sealed record FiducialDetection(Point2D Center, double ShapeError, Point2D[] Contour);

public sealed class FiducialDetectionException : InvalidOperationException
{
    public FiducialDetectionException(string message) : base(message)
    {
    }
}

public static class FiducialDetector
{
    public static FiducialDetection Detect(ImageFrame frame, FiducialMark mark, bool registering = false)
    {
        if (mark.Region is not { } region || !region.IsInside(frame.Width, frame.Height))
            throw new InvalidOperationException(UiText.Get("Draw the fiducial region"));
        if (frame.Stride < frame.Width * 3 || frame.Pixels.LongLength < (long)frame.Stride * frame.Height)
            throw new ArgumentException(UiText.Get("Invalid image buffer."), nameof(frame));
        if (!registering && (mark.Contour.Length < 5 || mark.ImageWidth != frame.Width || mark.ImageHeight != frame.Height))
            throw new InvalidOperationException(UiText.Get("Register the fiducial contour"));

        using var image = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        for (var row = 0; row < frame.Height; row++)
            Marshal.Copy(frame.Pixels, row * frame.Stride, image.Ptr(row), frame.Width * 3);
        using var gray = image.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var binary = gray.Threshold(mark.Threshold, 255, mark.WhiteMark ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv);
        var margin = registering ? 0 : mark.SearchMarginPixels;
        var left = Math.Max(0L, (long)region.X - margin);
        var top = Math.Max(0L, (long)region.Y - margin);
        var right = Math.Min(frame.Width, (long)region.X + region.Width + margin);
        var bottom = Math.Min(frame.Height, (long)region.Y + region.Height + margin);
        var search = new Rect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        using var cropped = new Mat(binary, search);
        var contours = cropped.FindContoursAsArray(RetrievalModes.External, ContourApproximationModes.ApproxNone,
            new Point(search.X, search.Y));
        using var reference = registering ? null
            : InputArray.Create(mark.Contour.Select(point => new Point2f((float)point.X, (float)point.Y)).ToArray());
        var referenceArea = registering ? 0 : Math.Abs(Cv2.ContourArea(reference!));
        if (!registering && (!double.IsFinite(referenceArea) || referenceArea < 4))
            throw new InvalidOperationException(UiText.Get("Register the fiducial contour"));
        var accepted = new List<FiducialDetection>();
        foreach (var contour in contours)
        {
            var bounds = Cv2.BoundingRect(contour);
            if (contour.Length < 5 || Cv2.ContourArea(contour) < 4
                || bounds.Left <= search.Left || bounds.Top <= search.Top
                || bounds.Right >= search.Right || bounds.Bottom >= search.Bottom)
                continue;
            // Reject non-circular features before comparing the taught hole's shape and size.
            var perimeter = Cv2.ArcLength(contour, true);
            if (4 * Math.PI * Cv2.ContourArea(contour) / (perimeter * perimeter) < 0.8)
                continue;
            var error = 0d;
            if (!registering)
            {
                // Area-derived size is independent of the mark's in-plane rotation.
                var sizeRatio = Math.Sqrt(Math.Abs(Cv2.ContourArea(contour)) / referenceArea);
                if (Math.Abs(sizeRatio - 1) * 100 > mark.SizeTolerancePercent)
                    continue;
                using var candidate = InputArray.Create(contour);
                error = Cv2.MatchShapes(reference!, candidate, ShapeMatchModes.I3);
                if (!double.IsFinite(error) || error > mark.MaximumShapeError)
                    continue;
            }

            var points = contour.Select(point => new Point2D(point.X, point.Y)).ToArray();
            accepted.Add(new(FitCircle(points), error, points));
        }
        return accepted.Count switch
        {
            1 => accepted[0],
            0 => throw new FiducialDetectionException(UiText.Get("Fiducial not found")),
            _ => throw new FiducialDetectionException(UiText.Get("Multiple fiducials found in the search region")),
        };
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
