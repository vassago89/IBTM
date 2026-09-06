using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed record CarrierScanImage(
    AxisPosition Center,
    ImageFrame Frame);

public sealed class BoltInspector(
    InspectionGantry gantry,
    ICamera camera,
    InspectionCameraSettings cameraSettings,
    ILightController light,
    BoltPresenceDetector presenceDetector,
    InspectionGantrySettings gantrySettings,
    CarrierReferenceSettings carrierReference,
    LightingSettings lightingSettings)
{
    public event Action<ImageFrame>? FrameReady
    {
        add => camera.FrameReady += value;
        remove => camera.FrameReady -= value;
    }

    public event Action<Exception>? LiveViewFailed
    {
        add => camera.LiveViewFailed += value;
        remove => camera.LiveViewFailed -= value;
    }

    public void InitializeVision()
    {
        light.Initialize();
        light.TurnOffAll();
        camera.Initialize();
    }

    public bool HasPosition(BoltPoint point) =>
        CarrierCoordinates.IsDefined(
            carrierReference.UpperLeftLocatingPin,
            carrierReference.LowerRightLocatingPin)
        && point is { X: not null, Y: not null };

    internal bool IsAt(BoltPoint point) =>
        gantry.IsAt(Position(point));

    public Task MoveToAsync(
        BoltPoint point,
        CancellationToken cancellationToken = default) =>
        MoveToAsync(Position(point), cancellationToken);

    private ImageFrame Capture()
    {
        TurnLightOn();
        try
        {
            return camera.Capture();
        }
        finally
        {
            TurnLightOff();
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        BoltPoint point,
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(point, cancellationToken);
        return await Task.Run(Capture, cancellationToken);
    }

    internal Task<bool> InspectAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => presenceDetector.IsPresent(Capture()),
            cancellationToken);

    public async Task<IReadOnlyList<CarrierScanImage>>
        CaptureCarrierImagesAsync(
            CancellationToken cancellationToken = default)
    {
        var overlap = gantrySettings.CarrierScanOverlapMillimeters;
        var xPositions = ScanPositions(
            gantrySettings.CarrierScanUpperLeft.X,
            gantrySettings.CarrierScanLowerRight.X,
            cameraSettings.FieldOfViewWidthMillimeters - overlap);
        var yPositions = ScanPositions(
            gantrySettings.CarrierScanUpperLeft.Y,
            gantrySettings.CarrierScanLowerRight.Y,
            cameraSettings.FieldOfViewHeightMillimeters - overlap);
        var images = new List<CarrierScanImage>(
            xPositions.Count * yPositions.Count);

        TurnLightOn();
        try
        {
            for (var row = 0; row < yPositions.Count; row++)
            {
                for (var column = 0; column < xPositions.Count; column++)
                {
                    var xIndex = row % 2 == 0
                        ? column
                        : xPositions.Count - column - 1;
                    var center = new AxisPosition
                    {
                        X = xPositions[xIndex],
                        Y = yPositions[row],
                    };
                    await MoveToAsync(center, cancellationToken)
                        .ConfigureAwait(false);
                    var frame = await Task.Run(
                            camera.Capture,
                            cancellationToken)
                        .ConfigureAwait(false);
                    images.Add(new(center, frame));
                }
            }
        }
        finally
        {
            TurnLightOff();
        }

        return images;
    }

    public void StartLiveView()
    {
        TurnLightOn();
        try
        {
            camera.StartLiveView();
        }
        catch
        {
            TurnLightOff();
            throw;
        }
    }

    public void StopLiveView()
    {
        try
        {
            camera.StopLiveView();
        }
        finally
        {
            TurnLightOff();
        }
    }

    private AxisPosition Position(BoltPoint point) =>
        gantrySettings.GetBoltPosition(point, carrierReference);

    private Task MoveToAsync(
        AxisPosition position,
        CancellationToken cancellationToken) =>
        gantry.MoveToAsync(
            position,
            gantrySettings.Motion.HorizontalSpeed,
            cancellationToken);

    private static IReadOnlyList<double> ScanPositions(
        double start,
        double end,
        double pitch)
    {
        var distance = Math.Abs(end - start);
        if (distance == 0)
        {
            return [start];
        }

        var segments = (int)Math.Ceiling(distance / pitch);
        var positions = new double[segments + 1];
        for (var index = 0; index <= segments; index++)
        {
            positions[index] =
                start + ((end - start) * index / segments);
        }

        return positions;
    }

    private void TurnLightOn()
    {
        var channel = lightingSettings.InspectionChannel;
        light.SetLevel(channel, lightingSettings.InspectionLevel);
        light.TurnOn(channel);
    }

    private void TurnLightOff() =>
        light.TurnOff(lightingSettings.InspectionChannel);
}
