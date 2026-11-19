using System;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed class InspectionGantrySettings : Setting
{
    public InspectionGantrySettings()
    {
        Motion = new();
    }

    public MotionSettings Motion { get; set; }
    public bool ExcludeDataMatrixFromNg { get; set; }
    public bool ExcludeFiducialFromNg { get; set; }

    public double? CameraResolution
    {
        get;
        set
        {
            if (value is { } resolution && (!double.IsFinite(resolution) || resolution <= 0))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Enter a positive camera resolution"));
            field = value;
        }
    }

    public Point2D ToMachinePosition(AxisPosition camera, Point2D pixel, int width, int height)
    {
        if (CameraResolution is not { } resolution)
            throw new InvalidOperationException(UiText.Get("Set camera resolution"));
        var x = pixel.X - width / 2d;
        var y = pixel.Y - height / 2d;
        // Image right is machine X+; image down is machine Y-.
        var position = new Point2D(camera.X + resolution * x, camera.Y - resolution * y);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new InvalidOperationException(UiText.Get("Invalid fiducial position"));
        return position;
    }

    public AxisPosition? GetFiducialPosition(FiducialMark mark)
    {
        if (mark.MarkPosition is { } center)
            return center;
        if (mark.LegacyCameraPosition is not { } camera || mark.Contour.Length < 5)
            return mark.LegacyCameraPosition;
        if (CameraResolution is null || mark.ImageWidth <= 0 || mark.ImageHeight <= 0)
            return null;

        // Older recipes stored the camera XY and the contour in the original frame.
        var position = ToMachinePosition(camera, FiducialDetector.FitCircle(mark.Contour), mark.ImageWidth, mark.ImageHeight);
        return new() { X = position.X, Y = position.Y };
    }

    // Axis travel from the camera-centred point to the same point under the probe.
    public double ProbeOffsetX
    {
        get;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite offset in millimetres."));
            field = value;
        }
    }

    public double ProbeOffsetY
    {
        get;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), UiText.Get("Use a finite offset in millimetres."));
            field = value;
        }
    }
}
