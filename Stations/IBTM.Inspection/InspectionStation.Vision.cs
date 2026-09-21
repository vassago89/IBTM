using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using System.Threading;
using System;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;

namespace IBTM.Inspection;

public sealed record CarrierImage(AxisPosition Center, ImageFrame Frame);

public sealed partial class InspectionStation
{
    private readonly ICamera _camera;
    private readonly ILightController _light;
    private readonly LightingSettings _lightingSettings;
    private readonly RecipeManager _recipes;
    private readonly SemaphoreSlim _visionGate;
    private int? _lightChannel;

    public event Action? LiveViewChanged;

    public event Action<ImageFrame, HeatSinkSlot, int?>? InspectionCaptured;

    public event Action<ImageFrame>? FrameReady
    {
        add => _camera.FrameReady += value;
        remove => _camera.FrameReady -= value;
    }

    public bool IsLiveView => _camera.IsLiveView;

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
            _camera.Initialize();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            _light.Initialize();
            _light.TurnOffAll();
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
            point.BrightnessThreshold ?? _recipes.Current.BoltInspection.BrightnessThreshold);
    }

    public bool HasBarcodePosition(HeatSinkSlot pcb)
    {
        return _recipes.Current.CarrierImages.Count(fov => fov.IsBarcode && fov.HeatSink == pcb) == 1;
    }

    public bool HasBarcodeRegion(HeatSinkSlot pcb)
    {
        var size = _camera.FrameSize;
        var fovs = _recipes.Current.CarrierImages.Where(fov => fov.IsBarcode && fov.HeatSink == pcb).ToArray();
        return fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetBarcodeFov(HeatSinkSlot pcb)
    {
        var fov = _recipes.Current.CarrierImages.SingleOrDefault(item => item.IsBarcode && item.HeatSink == pcb);
        if (fov is null)
            throw new InvalidOperationException($"Record a position for {pcb.GetDescription()} Data Matrix.");
        return fov;
    }

    public bool IsAtBarcode(HeatSinkSlot pcb, bool live = true)
    {
        return _transfer.IsAt(GetBarcodeFov(pcb).Center, live);
    }

    public Task MoveToBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken = default)
    {
        return _transfer.MoveToAsync(GetBarcodeFov(pcb).Center, cancellationToken: cancellationToken);
    }

    public async Task<ImageFrame> CaptureBarcodeAsync(
        HeatSinkSlot pcb,
        CancellationToken cancellationToken = default)
    {
        if (!HasBarcodeRegion(pcb))
            throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
        await MoveToBarcodeAsync(pcb, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    public async Task<ImageFrame> CaptureCurrentAsync(
        CancellationToken cancellationToken = default,
        bool keepLiveView = false)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = keepLiveView && _camera.IsLiveView
                ? await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false)
                : await CaptureWithLightAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    internal async Task<string?> ReadBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken)
    {
        var image = await CaptureBarcodeAsync(pcb, cancellationToken);
        var region = GetBarcodeFov(pcb).Region!;
        InspectionCaptured?.Invoke(image, pcb, null);
        var text = await Task.Run(() => DataMatrixReader.Read(image, region), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }

    public bool HasPosition(BoltPoint point)
    {
        return _recipes.Current.CarrierImages.Count(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink) == 1;
    }

    public bool HasRegion(BoltPoint point)
    {
        var size = _camera.FrameSize;
        var fovs = _recipes.Current.CarrierImages.Where(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink).ToArray();
        return fovs.Length == 1
            && fovs[0].Region is { } region
            && region.IsInside(size.Width, size.Height);
    }

    public CarrierImageTile GetFov(BoltPoint point)
    {
        var fov = _recipes.Current.CarrierImages.SingleOrDefault(fov =>
            !fov.IsBarcode
            && fov.BoltNumber == point.Number
            && fov.HeatSink == point.HeatSink);
        if (fov is null)
            throw new InvalidOperationException(
                $"Record a position for {point.HeatSink.GetDescription()} bolt {point.Number}.");
        return fov;
    }

    internal bool IsAt(BoltPoint point, bool live = true)
    {
        return _transfer.IsAt(GetFov(point).Center, live);
    }

    public Task MoveToAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        return _transfer.MoveToAsync(GetFov(point).Center, cancellationToken: cancellationToken);
    }

    private async Task<ImageFrame> CaptureWithLightAsync(CancellationToken cancellationToken)
    {
        await Task.Run(StopLiveView, cancellationToken).ConfigureAwait(false);
        var channel = _lightingSettings.InspectionChannel;
        Exception? failure = null;
        try
        {
            await Task.Run(() => TurnLightOn(channel), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_lightingSettings.StabilizationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            return await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false);
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
        if (!HasRegion(point))
            throw new InvalidOperationException(
                $"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Number}.");
        await MoveToAsync(point, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken);
    }

    internal async Task<bool> InspectAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        var image = await CaptureAsync(point, cancellationToken).ConfigureAwait(false);
        var region = GetFov(point).Region!;
        InspectionCaptured?.Invoke(image, point.HeatSink, point.Number);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var present = Check(image, region, point).BrightRatio
                    >= (point.MinimumBrightRatio ?? _recipes.Current.BoltInspection.MinimumBrightRatio);
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
                    var feedback = _transfer.Feedback;
                    if (feedback.IsMoving
                        || !feedback.GetAxisState(MotionAxis.X).InPosition
                        || !feedback.GetAxisState(MotionAxis.Y).InPosition)
                        throw new InvalidOperationException("Stop jogging before adding a map image.");

                    var position = feedback.GetPosition();
                    var center = new AxisPosition { X = position.X, Y = position.Y };
                    var frame = _camera.IsLiveView
                        ? await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false)
                        : await CaptureWithLightAsync(cancellationToken).ConfigureAwait(false);
                    if (!_transfer.IsAt(center))
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
            TurnLightOn(_lightingSettings.InspectionChannel);
            cancellationToken.ThrowIfCancellationRequested();
            _camera.StartLiveView();
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
            _camera.StopLiveView();
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
        System.Diagnostics.Trace.TraceError("Inspection live view failed. {0}", failure);
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
            _light.TurnOff(channel);
            _lightChannel = null;
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private void TurnLightOn(int channel)
    {
        _light.Initialize();
        _lightChannel = channel;
        _light.SetLevel(channel, _recipes.Current.BoltInspection.LightLevel);
        _light.TurnOn(channel);
    }
}
