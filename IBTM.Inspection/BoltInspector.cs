using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed record CarrierScanImage(AxisPosition Center, ImageFrame Frame);

public sealed class BoltInspector(
    InspectionGantry gantry,
    ICamera camera,
    ILightController light,
    BoltPresenceDetector presenceDetector,
    InspectionGantrySettings gantrySettings,
    CarrierReferenceSettings carrierReference,
    LightingSettings lightingSettings,
    Func<BoltInspectionRecipe> getRecipe,
    Func<PcbLayout> getPcb,
    Func<double> getMillimetersPerPixel)
{
    private int? _liveLightChannel;

    public event Action<BoltInspectionImage>? Inspected;

    public event Action<ImageFrame>? FrameReady
    {
        add
        {
            camera.FrameReady += value;
        }

        remove
        {
            camera.FrameReady -= value;
        }
    }

    public event Action<Exception>? LiveViewFailed
    {
        add
        {
            camera.LiveViewFailed += value;
        }

        remove
        {
            camera.LiveViewFailed -= value;
        }
    }

    public void InitializeVision()
    {
        light.Initialize();
        light.TurnOffAll();
        camera.Initialize();
    }

    public void CheckReady()
    {
        presenceDetector.CheckReady();
    }

    public BoltPrediction Predict(ImageFrame image)
    {
        return presenceDetector.Predict(image);
    }

    public (double Width, double Height) FieldOfView
    {
        get
        {
            return GetFieldOfView(camera.FrameSize);
        }
    }

    public (double Width, double Height) GetFieldOfView((int Width, int Height) frameSize)
    {
        return (frameSize.Width * getMillimetersPerPixel(), frameSize.Height * getMillimetersPerPixel());
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        return carrierReference.IsDefined
            && getPcb().GetDataMatrix(pcb) is { } region
            && region.Width > 0
            && region.Height > 0
            && region.Width <= FieldOfView.Width
            && region.Height <= FieldOfView.Height;
    }

    public AxisPosition BarcodePosition(HeatSinkSlot pcb)
    {
        return CarrierCoordinates.ToMachine(
            getPcb().GetDataMatrix(pcb)!.Center,
            carrierReference.UpperLeftLocatingPin!);
    }

    public bool IsAtBarcode(HeatSinkSlot pcb)
    {
        return gantry.IsAt(BarcodePosition(pcb));
    }

    public Task MoveToBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(BarcodePosition(pcb), cancellationToken);
    }

    public async Task<ImageFrame> CaptureBarcodeAsync(
        HeatSinkSlot pcb,
        CancellationToken cancellationToken = default)
    {
        await MoveToBarcodeAsync(pcb, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    public string? ReadBarcode(ImageFrame image)
    {
        var (width, height) = BarcodePixelSize();
        return DataMatrixReader.Read(image, width, height);
    }

    public (int Width, int Height) BarcodePixelSize()
    {
        var region = getPcb().DataMatrix!;
        return (
            (int)Math.Ceiling(region.Width / getMillimetersPerPixel()),
            (int)Math.Ceiling(region.Height / getMillimetersPerPixel()));
    }

    public async Task<ImageFrame> CaptureCurrentAsync(CancellationToken cancellationToken = default)
    {
        var frame = await Task.Run(Capture, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return frame;
    }

    internal async Task<string> ReadBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken)
    {
        var image = await CaptureCurrentAsync(cancellationToken);
        var text = await Task.Run(() => ReadBarcode(image), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new InvalidOperationException(
                $"Data Matrix could not be read for {pcb.GetDescription()}. Check the PCB and camera image.");
    }

    public bool HasPosition(BoltTarget point)
    {
        return CarrierCoordinates.IsDefined(
            carrierReference.UpperLeftLocatingPin,
            carrierReference.LowerRightLocatingPin)
            && point is { X: not null, Y: not null };
    }

    internal bool IsAt(BoltTarget point)
    {
        return gantry.IsAt(Position(point));
    }

    public Task MoveToAsync(BoltTarget point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(Position(point), cancellationToken);
    }

    private ImageFrame Capture()
    {
        var channel = lightingSettings.InspectionChannel;
        try
        {
            TurnLightOn(channel);
            return CaptureFrame();
        }
        finally
        {
            light.TurnOff(channel);
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        BoltTarget point,
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(point, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    internal Task<bool> InspectAsync(BoltTarget point, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var image = Capture();
                var capturedAt = DateTimeOffset.UtcNow;
                cancellationToken.ThrowIfCancellationRequested();
                var present = presenceDetector.IsPresent(image);
                cancellationToken.ThrowIfCancellationRequested();
                Inspected?.Invoke(
                    new(
                        image,
                        point.Number,
                        point.HeatSink,
                        getRecipe().RegionSizePixels,
                        present,
                        capturedAt));
                return present;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<CarrierScanImage>> CaptureCarrierImagesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var overlap = getRecipe().CarrierScanOverlapMillimeters;
        var (width, height) = FieldOfView;
        var xPositions = ScanPositions(
            carrierReference.UpperLeftLocatingPin!.X,
            carrierReference.LowerRightLocatingPin!.X,
            width - overlap);
        var yPositions = ScanPositions(
            carrierReference.UpperLeftLocatingPin.Y,
            carrierReference.LowerRightLocatingPin.Y,
            height - overlap);
        var images = new List<CarrierScanImage>(xPositions.Count * yPositions.Count);
        var channel = lightingSettings.InspectionChannel;

        try
        {
            TurnLightOn(channel);
            for (var row = 0; row < yPositions.Count; row++)
            {
                for (var column = 0; column < xPositions.Count; column++)
                {
                    var xIndex = row % 2 == 0 ? column : xPositions.Count - column - 1;
                    var center = new AxisPosition
                    {
                        X = xPositions[xIndex],
                        Y = yPositions[row],
                    };
                    await MoveToAsync(center, cancellationToken).ConfigureAwait(false);
                    var frame = await Task.Run(CaptureFrame, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    images.Add(new(center, frame));
                }
            }
        }
        finally
        {
            light.TurnOff(channel);
        }

        return images;
    }

    public void StartLiveView()
    {
        if (_liveLightChannel is not null)
            StopLiveView();

        var channel = lightingSettings.InspectionChannel;
        _liveLightChannel = channel;
        try
        {
            TurnLightOn(channel);
            var recipe = getRecipe();
            camera.StartLiveView(recipe.ExposureMicroseconds, recipe.Gain);
        }
        catch
        {
            StopLiveView();
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
            if (_liveLightChannel is { } channel)
            {
                light.TurnOff(channel);
                _liveLightChannel = null;
            }
        }
    }

    private AxisPosition Position(BoltTarget point)
    {
        return gantrySettings.GetBoltPosition(point, carrierReference);
    }

    private Task MoveToAsync(AxisPosition position, CancellationToken cancellationToken)
    {
        return gantry.MoveToAsync(position, gantrySettings.Motion.HorizontalSpeed, cancellationToken);
    }

    private static IReadOnlyList<double> ScanPositions(double start, double end, double pitch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pitch);
        var distance = Math.Abs(end - start);
        if (distance == 0)
        {
            return [start];
        }

        var segments = (int)Math.Ceiling(distance / pitch);
        var positions = new double[segments + 1];
        for (var index = 0; index <= segments; index++)
        {
            positions[index] = start + ((end - start) * index / segments);
        }

        return positions;
    }

    private void TurnLightOn(int channel)
    {
        light.SetLevel(channel, getRecipe().LightLevel);
        light.TurnOn(channel);
    }

    private ImageFrame CaptureFrame()
    {
        var recipe = getRecipe();
        return camera.Capture(recipe.ExposureMicroseconds, recipe.Gain);
    }
}
