using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed record CarrierScanImage(AxisPosition Center, ImageFrame Frame);

public sealed class BoltInspector
{
    private readonly InspectionGantry gantry;
    private readonly ICamera camera;
    private readonly ILightController light;
    private readonly BoltPresenceDetector presenceDetector;
    private readonly InspectionGantrySettings gantrySettings;
    private readonly CarrierReferenceSettings carrierReference;
    private readonly LightingSettings lightingSettings;
    private readonly Func<BoltInspectionRecipe> getRecipe;
    private readonly Func<PcbLayout> getPcb;
    private readonly Func<double> getMillimetersPerPixel;
    private readonly SemaphoreSlim _visionGate = new(1, 1);
    private volatile bool _isLiveView;
    private int? _lightChannel;

    public BoltInspector(
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
        this.gantry = gantry;
        this.camera = camera;
        this.light = light;
        this.presenceDetector = presenceDetector;
        this.gantrySettings = gantrySettings;
        this.carrierReference = carrierReference;
        this.lightingSettings = lightingSettings;
        this.getRecipe = getRecipe;
        this.getPcb = getPcb;
        this.getMillimetersPerPixel = getMillimetersPerPixel;
        camera.LiveViewFailed += OnCameraLiveViewFailed;
    }

    public event Action<BoltInspectionImage>? Inspected;
    public event Action? LiveViewChanged;

    public bool IsLiveView
    {
        get
        {
            return _isLiveView;
        }
    }

    public Exception? LiveViewError { get; private set; }

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

    public async Task InitializeVisionAsync(CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(InitializeVision, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void InitializeVision()
    {
        // Recovery does not require a successful OFF on a disconnected device.
        // Each driver first restores its connection; camera initialization leaves acquisition stopped.
        SetLiveViewState(false);
        Exception? failure = null;
        try
        {
            camera.Initialize();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            light.Initialize();
            light.TurnOffAll();
            _lightChannel = null;
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        SetLiveViewState(false, failure);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
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
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = await Task.Run(Capture, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        finally
        {
            _visionGate.Release();
        }
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
        return gantry.IsAt(gantrySettings.GetBoltPosition(point, carrierReference));
    }

    public Task MoveToAsync(BoltTarget point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(gantrySettings.GetBoltPosition(point, carrierReference), cancellationToken);
    }

    private ImageFrame Capture()
    {
        StopLiveView();
        var channel = lightingSettings.InspectionChannel;
        Exception? failure = null;
        try
        {
            TurnLightOn(channel);
            return CaptureFrame();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            TurnLightOff(channel, failure);
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        BoltTarget point,
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(point, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    internal async Task<bool> InspectAsync(BoltTarget point, CancellationToken cancellationToken = default)
    {
        var image = await CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
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
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => CaptureCarrierImagesCoreAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private async Task<IReadOnlyList<CarrierScanImage>> CaptureCarrierImagesCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopLiveView();
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
        Exception? failure = null;

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
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            TurnLightOff(channel, failure);
        }

        return images;
    }

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => StartLiveView(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException
                && cancellationToken.IsCancellationRequested;
            SetLiveViewState(false, cancelled ? null : exception);
            throw;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void StartLiveView(CancellationToken cancellationToken)
    {
        StopLiveView();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetLiveViewState(true);
            TurnLightOn(lightingSettings.InspectionChannel);
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = getRecipe();
            camera.StartLiveView(recipe.ExposureMicroseconds, recipe.Gain);
            cancellationToken.ThrowIfCancellationRequested();
            if (LiveViewError is { } failure)
                ExceptionDispatchInfo.Throw(failure);
        }
        catch (Exception failure)
        {
            try
            {
                StopLiveView();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            throw;
        }
    }

    public async Task StopLiveViewAsync()
    {
        await _visionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(StopLiveView).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetLiveViewState(false, exception);
            throw;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    private void StopLiveView()
    {
        SetLiveViewState(false);
        Exception? failure = null;
        try
        {
            camera.StopLiveView();
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (_lightChannel is { } channel)
                TurnLightOff(channel, failure);
        }
    }

    private void OnCameraLiveViewFailed(Exception failure)
    {
        // The driver has stopped acquisition. Finish cleanup on the receive thread;
        // the next camera operation joins that thread before touching the light.
        try
        {
            if (_lightChannel is { } channel)
                TurnLightOff(channel, failure);
        }
        catch (Exception cleanupFailure)
        {
            failure = cleanupFailure;
        }
        SetLiveViewState(false, failure);
        Trace.TraceError("Inspection live view failed. {0}", failure);
    }

    private void SetLiveViewState(bool live, Exception? failure = null)
    {
        if (_isLiveView == live && ReferenceEquals(LiveViewError, failure))
            return;

        LiveViewError = failure;
        _isLiveView = live;
        LiveViewChanged?.Invoke();
    }

    private void TurnLightOff(int channel, Exception? failure)
    {
        try
        {
            light.TurnOff(channel);
            _lightChannel = null;
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
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
        _lightChannel = channel;
        light.SetLevel(channel, getRecipe().LightLevel);
        light.TurnOn(channel);
    }

    private ImageFrame CaptureFrame()
    {
        var recipe = getRecipe();
        return camera.Capture(recipe.ExposureMicroseconds, recipe.Gain);
    }
}
