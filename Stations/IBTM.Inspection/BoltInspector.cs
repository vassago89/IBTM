using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.Inspection;

public sealed record CarrierImage(AxisPosition Center, ImageFrame Frame);

public sealed class BoltInspector
{
    private readonly InspectionGantry gantry;
    private readonly ICamera camera;
    private readonly ILightController light;
    private readonly BoltPresenceDetector presenceDetector;
    private readonly InspectionGantrySettings gantrySettings;
    private readonly LightingSettings lightingSettings;
    private readonly Func<BoltInspectionRecipe> getRecipe;
    private readonly Func<IReadOnlyList<CarrierImageTile>> getFovs;
    private readonly SemaphoreSlim _visionGate = new(1, 1);
    private int? _lightChannel;

    public BoltInspector(
        InspectionGantry gantry,
        ICamera camera,
        ILightController light,
        BoltPresenceDetector presenceDetector,
        InspectionGantrySettings gantrySettings,
        LightingSettings lightingSettings,
        Func<BoltInspectionRecipe> getRecipe,
        Func<IReadOnlyList<CarrierImageTile>> getFovs)
    {
        this.gantry = gantry;
        this.camera = camera;
        this.light = light;
        this.presenceDetector = presenceDetector;
        this.gantrySettings = gantrySettings;
        this.lightingSettings = lightingSettings;
        this.getRecipe = getRecipe;
        this.getFovs = getFovs;
        camera.LiveViewFailed += OnCameraLiveViewFailed;
    }

    public event Action<BoltInspectionImage>? Inspected;
    public event Action? LiveViewChanged;

    public bool IsLiveView
    {
        get
        {
            return camera.IsLiveView;
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

        PublishLiveView(failure);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    public void CheckReady()
    {
        presenceDetector.CheckReady();
    }

    public BoltPrediction Predict(ImageFrame image, PixelRegion region)
    {
        return presenceDetector.Predict(image, region);
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        var size = camera.FrameSize;
        var fovs = getFovs().Where(fov => fov.IsBarcode && fov.HeatSink == pcb).ToArray();
        return fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetBarcodeFov(HeatSinkSlot pcb)
    {
        var fov = getFovs().SingleOrDefault(item => item.IsBarcode && item.HeatSink == pcb);
        var size = camera.FrameSize;
        if (fov?.Region is not { } region || !region.IsInside(size.Width, size.Height))
            throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
        return fov;
    }

    public bool IsAtBarcode(HeatSinkSlot pcb)
    {
        return gantry.IsAt(GetBarcodeFov(pcb).Center);
    }

    public Task MoveToBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(GetBarcodeFov(pcb).Center, cancellationToken);
    }

    public async Task<ImageFrame> CaptureBarcodeAsync(
        HeatSinkSlot pcb,
        CancellationToken cancellationToken = default)
    {
        await MoveToBarcodeAsync(pcb, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
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
        var region = GetBarcodeFov(pcb).Region!;
        var image = await CaptureCurrentAsync(cancellationToken);
        var text = await Task.Run(() => DataMatrixReader.Read(image, region), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return !string.IsNullOrEmpty(text)
            ? text
            : throw new InvalidOperationException(
                $"Data Matrix could not be read for {pcb.GetDescription()}. Check the PCB and camera image.");
    }

    public bool HasPosition(BoltTarget point)
    {
        return getFovs().Count(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink
            && fov.Region is not null) == 1;
    }

    public CarrierImageTile GetFov(BoltTarget point)
    {
        return getFovs().SingleOrDefault(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink
            && fov.Region is not null)
            ?? throw new InvalidOperationException(
                $"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Number}.");
    }

    internal bool IsAt(BoltTarget point)
    {
        return gantry.IsAt(GetFov(point).Center);
    }

    public Task MoveToAsync(BoltTarget point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(GetFov(point).Center, cancellationToken);
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
        var region = GetFov(point).Region!;
        var image = await CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                var capturedAt = DateTimeOffset.UtcNow;
                cancellationToken.ThrowIfCancellationRequested();
                var present = presenceDetector.IsPresent(image, region);
                cancellationToken.ThrowIfCancellationRequested();
                Inspected?.Invoke(
                    new(
                        image,
                        point.Number,
                        point.HeatSink,
                        IBoltRecessSegmenter.InputSize,
                        present,
                        capturedAt,
                        region));
                return present;
            },
            cancellationToken);
    }

    public async Task<CarrierImage> CaptureCarrierImageAsync(
        CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var image = await Task.Run(
                () =>
                {
                    var feedback = gantry.Feedback;
                    if (feedback.IsMoving
                        || !feedback.GetAxisState(MotionAxis.X).InPosition
                        || !feedback.GetAxisState(MotionAxis.Y).InPosition)
                        throw new InvalidOperationException("Stop jogging before adding a map image.");

                    var position = feedback.GetPosition();
                    var center = new AxisPosition { X = position.X, Y = position.Y };
                    var frame = camera.IsLiveView ? CaptureFrame() : Capture();
                    if (!gantry.IsAt(center))
                        throw new InvalidOperationException("The gantry moved during capture. Stop jogging and capture the map image again.");
                    return new CarrierImage(center, frame);
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        finally
        {
            _visionGate.Release();
        }
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
            PublishLiveView(cancelled ? null : exception);
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
            TurnLightOn(lightingSettings.InspectionChannel);
            cancellationToken.ThrowIfCancellationRequested();
            var recipe = getRecipe();
            camera.StartLiveView(recipe.ExposureMicroseconds, recipe.Gain);
            cancellationToken.ThrowIfCancellationRequested();
            if (LiveViewError is { } failure)
                ExceptionDispatchInfo.Throw(failure);
            LiveViewChanged?.Invoke();
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
        finally
        {
            _visionGate.Release();
        }
    }

    private void StopLiveView()
    {
        Exception? failure = null;
        try
        {
            camera.StopLiveView();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            if (_lightChannel is { } channel)
                TurnLightOff(channel, failure);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        PublishLiveView(failure);
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
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
        PublishLiveView(failure);
        Trace.TraceError("Inspection live view failed. {0}", failure);
    }

    private void PublishLiveView(Exception? failure = null)
    {
        LiveViewError = failure;
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
