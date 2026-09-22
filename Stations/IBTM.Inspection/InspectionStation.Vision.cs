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
        return IsAt(GetBarcodeFov(pcb).Center, live);
    }

    public Task MoveToBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(GetBarcodeFov(pcb).Center, cancellationToken: cancellationToken);
    }

    public async Task<ImageFrame> CaptureBarcodeAsync(
        HeatSinkSlot pcb,
        CancellationToken cancellationToken = default)
    {
        if (!HasBarcodeRegion(pcb))
            throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
        await MoveToBarcodeAsync(pcb, cancellationToken);
        return await CaptureCurrentAsync(
            cancellationToken, lightLevel: _recipes.Current.BoltInspection.GetDataMatrix(pcb).LightLevel);
    }

    public async Task<ImageFrame> CaptureCurrentAsync(
        CancellationToken cancellationToken = default,
        bool keepLiveView = false,
        int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = await CaptureWithLightAsync(lightLevel, cancellationToken, keepLiveView).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        finally
        {
            _visionGate.Release();
        }
    }

    internal async Task<InspectionCapture> ReadBarcodeAsync(HeatSinkSlot pcb, CancellationToken cancellationToken)
    {
        (AxisPosition Center, PixelRegion Region, DataMatrixInspectionRecipe Decoder, int Light) settings;
        lock (_recipes.InspectionSync)
        {
            if (!HasBarcodeRegion(pcb))
                throw new InvalidOperationException($"Teach a FOV and ROI for {pcb.GetDescription()} Data Matrix.");
            var fov = GetBarcodeFov(pcb);
            var decoder = _recipes.Current.BoltInspection.GetDataMatrix(pcb);
            settings = (fov.Center, fov.Region!, decoder, decoder.LightLevel ?? _recipes.Current.BoltInspection.LightLevel);
        }
        await MoveToAsync(settings.Center, cancellationToken: cancellationToken);
        var image = await CaptureCurrentAsync(cancellationToken, lightLevel: settings.Light);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, pcb, null);
        var text = await Task.Run(() => DataMatrixReader.Read(image, settings.Region, settings.Decoder), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(null, capturedAt, image, settings.Region, !string.IsNullOrEmpty(text), Barcode: text);
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
        return IsAt(GetFov(point).Center, live);
    }

    public Task MoveToBoltAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        return MoveToAsync(GetFov(point).Center, cancellationToken: cancellationToken);
    }

    private async Task<ImageFrame> CaptureWithLightAsync(
        int? lightLevel, CancellationToken cancellationToken, bool keepLiveView = false)
    {
        if (keepLiveView && _camera.IsLiveView)
        {
            var liveChannel = _lightChannel ?? _lightingSettings.InspectionChannel;
            await Task.Run(() => TurnLightOn(liveChannel, lightLevel), cancellationToken).ConfigureAwait(false);
            await Task.Delay(_lightingSettings.StabilizationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            return await _camera.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }

        await Task.Run(StopLiveView, cancellationToken).ConfigureAwait(false);
        var channel = _lightingSettings.InspectionChannel;
        Exception? failure = null;
        try
        {
            await Task.Run(() => TurnLightOn(channel, lightLevel), cancellationToken).ConfigureAwait(false);
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
        await MoveToBoltAsync(point, cancellationToken);
        return await CaptureCurrentAsync(cancellationToken, lightLevel: point.LightLevel);
    }

    internal async Task<InspectionCapture> InspectAsync(BoltPoint point, CancellationToken cancellationToken = default)
    {
        (AxisPosition Center, PixelRegion Region, int Light, int Threshold, double Minimum) settings;
        lock (_recipes.InspectionSync)
        {
            if (!HasRegion(point))
                throw new InvalidOperationException($"Teach a FOV and ROI for {point.HeatSink.GetDescription()} bolt {point.Number}.");
            var fov = GetFov(point);
            var defaults = _recipes.Current.BoltInspection;
            settings = (fov.Center, fov.Region!, point.LightLevel ?? defaults.LightLevel,
                point.BrightnessThreshold ?? defaults.BrightnessThreshold,
                point.MinimumBrightRatio ?? defaults.MinimumBrightRatio);
        }
        await MoveToAsync(settings.Center, cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = await CaptureCurrentAsync(cancellationToken, lightLevel: settings.Light).ConfigureAwait(false);
        var capturedAt = DateTimeOffset.Now;
        InspectionCaptured?.Invoke(image, point.HeatSink, point.Number);
        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ratio = BinaryChecker.Check(image, settings.Region, settings.Threshold).BrightRatio;
                cancellationToken.ThrowIfCancellationRequested();
                return new InspectionCapture(point.Number, capturedAt, image, settings.Region, ratio >= settings.Minimum,
                    BrightRatio: ratio, MinimumBrightRatio: settings.Minimum);
            },
            cancellationToken);
    }

    public async Task<CarrierImage> CaptureCarrierImageAsync(
        CancellationToken cancellationToken = default,
        int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var image = await Task.Run(
                async () =>
                {
                    var feedback = Feedback;
                    if (feedback.IsMoving
                        || !feedback.GetAxisState(MotionAxis.X).InPosition
                        || !feedback.GetAxisState(MotionAxis.Y).InPosition)
                        throw new InvalidOperationException("Stop jogging before adding a map image.");

                    var position = feedback.GetPosition();
                    var center = new AxisPosition { X = position.X, Y = position.Y };
                    var frame = await CaptureWithLightAsync(lightLevel, cancellationToken, keepLiveView: true).ConfigureAwait(false);
                    if (!IsAt(center))
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

    public async Task StartLiveViewAsync(CancellationToken cancellationToken = default, int? lightLevel = null)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => StartLiveView(lightLevel, cancellationToken), cancellationToken).ConfigureAwait(false);
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

    private void StartLiveView(int? lightLevel, CancellationToken cancellationToken)
    {
        StopLiveView();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TurnLightOn(_lightingSettings.InspectionChannel, lightLevel);
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

    public async Task ApplyLiveLightAsync(int? lightLevel, CancellationToken cancellationToken = default)
    {
        await _visionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsLiveView && _lightChannel is { } channel)
                await Task.Run(() => TurnLightOn(channel, lightLevel), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _visionGate.Release();
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

    private void TurnLightOn(int channel, int? lightLevel)
    {
        _light.Initialize();
        _lightChannel = channel;
        _light.SetLevel(channel, lightLevel ?? _recipes.Current.BoltInspection.LightLevel);
        _light.TurnOn(channel);
    }
}
