using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.Inspection;

public sealed record CarrierImage(AxisPosition Center, ImageFrame Frame);

public sealed class BoltInspector
{
    private readonly InspectionGantry gantry;
    private readonly ICamera camera;
    private readonly ILightController light;
    private readonly InspectionGantrySettings gantrySettings;
    private readonly LightingSettings lightingSettings;
    private readonly RecipeManager recipes;
    private readonly SemaphoreSlim _visionGate;
    private int? _lightChannel;

    public BoltInspector(
        InspectionGantry gantry,
        ICamera camera,
        ILightController light,
        InspectionGantrySettings gantrySettings,
        LightingSettings lightingSettings,
        RecipeManager recipes)
    {
        _visionGate = new(1, 1);

        this.gantry = gantry;
        this.camera = camera;
        this.light = light;
        this.gantrySettings = gantrySettings;
        this.lightingSettings = lightingSettings;
        this.recipes = recipes;
        camera.LiveViewFailed += OnCameraLiveViewFailed;
    }

    public event Action? LiveViewChanged;

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

    public bool IsLiveView => camera.IsLiveView;

    public Exception? LiveViewError { get; private set; }

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

    public BinaryCheckResult Check(ImageFrame image, PixelRegion region, BoltPoint point)
    {
        return BinaryChecker.Check(image, region,
            point.BrightnessThreshold ?? recipes.Current.BoltInspection.BrightnessThreshold);
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        var size = camera.FrameSize;
        var fovs = recipes.Current.CarrierImages.Where(fov => fov.IsBarcode && fov.HeatSink == pcb).ToArray();
        return fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetBarcodeFov(HeatSinkSlot pcb)
    {
        var fov = recipes.Current.CarrierImages.SingleOrDefault(item => item.IsBarcode && item.HeatSink == pcb);
        var size = camera.FrameSize;
        if (fov?.Region is not { } region || !region.IsInside(size.Width, size.Height))
            throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
        return fov;
    }

    public bool IsAtBarcode(HeatSinkSlot pcb, bool live = true)
    {
        return gantry.IsAt(GetBarcodeFov(pcb).Center, live);
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
            var frame = await CaptureWithLightAsync(cancellationToken).ConfigureAwait(false);
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

    public bool HasPosition(BoltPoint point)
    {
        var size = camera.FrameSize;
        var fovs = recipes.Current.CarrierImages.Where(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink).ToArray();
        return fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetFov(BoltPoint point)
    {
        var fov = recipes.Current.CarrierImages.SingleOrDefault(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink);
        var size = camera.FrameSize;
        if (fov?.Region is not { } region || !region.IsInside(size.Width, size.Height))
            throw new InvalidOperationException(
                $"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Number}.");
        return fov;
    }

    internal bool IsAt(BoltPoint point, bool live = true)
    {
        return gantry.IsAt(GetFov(point).Center, live);
    }

    public Task MoveToAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(GetFov(point).Center, cancellationToken);
    }

    private async Task<ImageFrame> CaptureWithLightAsync(CancellationToken cancellationToken)
    {
        await Task.Run(StopLiveView, cancellationToken).ConfigureAwait(false);
        var channel = lightingSettings.InspectionChannel;
        Exception? failure = null;
        try
        {
            await Task.Run(() => TurnLightOn(channel), cancellationToken).ConfigureAwait(false);
            return await CaptureFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await Task.Run(() => TurnLightOff(channel, failure)).ConfigureAwait(false);
        }
    }

    public async Task<ImageFrame> CaptureAsync(
        BoltPoint point,
        CancellationToken cancellationToken = default)
    {
        await MoveToAsync(point, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    internal async Task<bool> InspectAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        var region = GetFov(point).Region!;
        var image = await CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var present = Check(image, region, point).BrightRatio
                    >= (point.MinimumBrightRatio ?? recipes.Current.BoltInspection.MinimumBrightRatio);
                cancellationToken.ThrowIfCancellationRequested();
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
                async () =>
                {
                    var feedback = gantry.Feedback;
                    if (feedback.IsMoving
                        || !feedback.GetAxisState(MotionAxis.X).InPosition
                        || !feedback.GetAxisState(MotionAxis.Y).InPosition)
                        throw new InvalidOperationException("Stop jogging before adding a map image.");

                    var position = feedback.GetPosition();
                    var center = new AxisPosition { X = position.X, Y = position.Y };
                    var frame = camera.IsLiveView
                        ? await CaptureFrameAsync(cancellationToken).ConfigureAwait(false)
                        : await CaptureWithLightAsync(cancellationToken).ConfigureAwait(false);
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
            var recipe = recipes.Current.BoltInspection;
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
        light.SetLevel(channel, recipes.Current.BoltInspection.LightLevel);
        light.TurnOn(channel);
    }

    private async Task<ImageFrame> CaptureFrameAsync(CancellationToken cancellationToken)
    {
        var recipe = recipes.Current.BoltInspection;
        return await camera.CaptureAsync(
            recipe.ExposureMicroseconds, recipe.Gain, cancellationToken).ConfigureAwait(false);
    }
}
