using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    private readonly object _liveImageGate = new();
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private Task _liveImageUpdate = Task.CompletedTask;
    private Task<Exception?> _cameraStop = Task.FromResult<Exception?>(null);

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private async Task ToggleLiveViewAsync(CancellationToken cancellationToken)
    {
        if (!CanToggleLiveView())
            return;

        if (IsCameraLive)
        {
            await StopCameraLiveAsync();
            return;
        }

        Preview.Clear(SelectedBarcode);
        CameraError = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ViewCancellation);
        try
        {
            await _boltInspector.StartLiveViewAsync(cancellation.Token);
            if (!_state.ManualMode || !IsInspectionSelected)
                await StopCameraLiveAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Camera live view start failed. {0}", exception);
        }
    }

    private bool CanToggleLiveView()
    {
        return IsCameraLive
            || IsInspectionSelected
                && _state.ManualMode
                && !CaptureCarrierImagesCommand.IsRunning
                && !CaptureInspectionCommand.IsRunning
                && !CollectBoltImagesCommand.IsRunning;
    }

    private void OnInspectionCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            ToggleLiveViewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCaptureCarrierImages))]
    private async Task CaptureCarrierImagesAsync(CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await RunInspectionAsync(
                async token =>
                {
                    _recipeImageCancellation.Cancel();
                    await _recipeImageUpdate;
                    token.ThrowIfCancellationRequested();
                    Preview.Clear(SelectedBarcode);
                    CarrierImages = [];
                    var captured = await _boltInspector.CaptureCarrierImagesAsync(token);
                    var images = await Task.Run(
                        () => captured.Select(
                            (image, index) => new CarrierImageTileView(
                                index + 1,
                                image.Center,
                                InspectionPreview.CreateBitmap(image.Frame)))
                            .ToArray(),
                        token);

                    token.ThrowIfCancellationRequested();
                    CarrierImages = images;
                    completed = await RecipeEditor.SaveCarrierImagesAsync(images, token);
                    if (!completed)
                        return;
                    token.ThrowIfCancellationRequested();
                    SelectedPoint = NextTeachingPoint() ?? FilteredPoints.FirstOrDefault(
                        point => point.Position.Target == TeachingTarget.BoltReference);
                },
                cancellationToken);
        }
        finally
        {
            if (!completed)
            {
                ShowRecipeImages();
                await _recipeImageUpdate;
            }
        }
    }

    private bool CanCaptureCarrierImages()
    {
        return IsInspectionSelected
            && _inspectionGantry.CanMove
            && _carrierReference.IsDefined
            && MillimetersPerPixel > 0
            && ScanOverlap >= 0
            && ScanOverlap < _boltInspector.FieldOfView.Width
            && ScanOverlap < _boltInspector.FieldOfView.Height
            && RecipeEditor.CanSave
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false);
    }

    [RelayCommand(CanExecute = nameof(CanTeachImagePoint))]
    private async Task TeachImagePointAsync(Point imagePoint)
    {
        if (!CanEditRecipe())
            return;
        if (SelectedPoint!.Position.Target == TeachingTarget.BoltReference)
            SelectedPcb = FindPcb(new Rect(imagePoint, new Size()))!.Value;
        var point = SelectedPoint!;
        point.Teach(imagePoint.X, imagePoint.Y, 0);
        point.Apply();
        RefreshPointPositions();
        await RecipeEditor.SaveAsync(ViewCancellation);
        NotifyManualTeachingCommands();
    }

    private bool CanTeachImagePoint(Point point)
    {
        return CanEditInspectionRecipe
            && RecipeEditor.CanSave
            && HasCarrierImages
            && !IsCameraLive
            && _carrierReference.IsDefined
            && (SelectedPoint?.Position.Target == TeachingTarget.BoltReference
                && FindPcb(new Rect(point, new Size())) is not null
                || SelectedPoint?.Position.Target == TeachingTarget.PcbRegion
                && SelectedPcb == HeatSinkSlot.HeatSink2
                && RecipeEditor.Recipe.Pcb.GetRegion(HeatSinkSlot.HeatSink1) is not null);
    }

    [RelayCommand(CanExecute = nameof(CanTeachImageRegion))]
    private async Task TeachImageRegionAsync(Rect bounds)
    {
        if (!CanEditRecipe())
            return;
        CameraError = null;
        var pin = _carrierReference.UpperLeftLocatingPin!;
        var layout = RecipeEditor.Recipe.Pcb;
        if (SelectedPoint!.Position.Target == TeachingTarget.PcbRegion)
        {
            layout.Width = bounds.Width;
            layout.Height = bounds.Height;
            layout.Origins[SelectedPcb] = new() { X = bounds.X - pin.X, Y = bounds.Y - pin.Y };
        }
        else
        {
            var (width, height) = ImageFieldOfView;
            if (bounds.Width > width || bounds.Height > height)
            {
                CameraError = "Data Matrix region must fit inside one camera FOV.";
                return;
            }

            SelectedPcb = FindPcb(bounds)!.Value;
            var origin = layout.Origins[SelectedPcb];
            layout.DataMatrix = new(
                bounds.X - pin.X - origin.X,
                bounds.Y - pin.Y - origin.Y,
                bounds.Width,
                bounds.Height);
        }

        RefreshPointPositions();
        await RecipeEditor.SaveAsync(ViewCancellation);
        NotifyManualTeachingCommands();
    }

    private bool CanTeachImageRegion(Rect bounds)
    {
        return CanEditInspectionRecipe
            && RecipeEditor.CanSave
            && HasCarrierImages
            && !IsCameraLive
            && _carrierReference.IsDefined
            && (SelectedPoint?.Position.Target == TeachingTarget.PcbRegion
                && SelectedPcb == HeatSinkSlot.HeatSink1
                || SelectedBarcode is not null
                && (bounds.IsEmpty
                    ? Enum.GetValues<HeatSinkSlot>()
                        .Any(pcb => RecipeEditor.Recipe.Pcb.GetRegion(pcb) is not null)
                    : FindPcb(bounds) is not null));
    }

    private HeatSinkSlot? FindPcb(Rect bounds)
    {
        var pin = _carrierReference.UpperLeftLocatingPin!;
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            if (RecipeEditor.Recipe.Pcb.GetRegion(pcb) is { } region
                && new Rect(region.X + pin.X, region.Y + pin.Y, region.Width, region.Height).Contains(
                    bounds))
                return pcb;
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanCaptureInspection))]
    private Task CaptureInspectionAsync(CancellationToken token)
    {
        return RunInspectionAsync(
            async ct =>
            {
                Preview.Clear(SelectedBarcode);
                var frame = SelectedBarcode is { } pcb
                    ? await _boltInspector.CaptureBarcodeAsync(pcb, ct)
                    : await _boltInspector.CaptureAsync(SelectedPoint!.Position.Bolt!, ct);
                await Preview.SetImageAsync(frame, ct);
                await Preview.InspectAsync(ct);
            },
            token);
    }

    private bool CanCaptureInspection()
    {
        return IsInspectionSelected
            && CanMoveToPoint()
            && (SelectedBarcode is { } pcb
                ? _boltInspector.HasBarcodeRegion(pcb)
                : SelectedPoint?.Position.Target == TeachingTarget.BoltReference);
    }

    [RelayCommand(CanExecute = nameof(CanReinspectImage))]
    private async Task ReinspectImageAsync(CancellationToken token)
    {
        CameraError = null;
        try
        {
            await Machine.RunTeachingEditAsync(Preview.InspectAsync, token, ViewCancellation);
        }
        catch (OperationCanceledException) when (
            token.IsCancellationRequested
            || ViewCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Image reinspection failed. {0}", exception);
            CameraError = exception.Message;
        }
    }

    private bool CanReinspectImage()
    {
        return CanEditInspectionRecipe && Preview.HasImage;
    }

    [RelayCommand(CanExecute = nameof(CanCollectBoltImages))]
    private Task CollectBoltImagesAsync(CancellationToken token)
    {
        return RunInspectionAsync(
            async ct =>
            {
                foreach (var point in RecipeEditor.Recipe.Pcb.GetBolts()
                    .Where(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
                    .OrderBy(point => point.HeatSink)
                    .ThenBy(point => point.Number))
                {
                    var frame = await _boltInspector.CaptureAsync(point, ct);
                    await Task.Run(
                        () => _trainingStore.AddImage(
                            $"{point.HeatSink.GetDescription()} · Bolt {point.Number}",
                            frame,
                            InspectionRecipe.RegionSizePixels),
                        ct);
                }
            },
            token);
    }

    private bool CanCollectBoltImages()
    {
        return IsInspectionSelected
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && _inspectionGantry.CanMove
            && RecipeEditor.Recipe.Pcb.GetBolts()
                .Any(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
            && RecipeEditor.Recipe.Pcb.GetBolts()
                .Where(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
                .All(_boltInspector.HasPosition);
    }

    private async Task RunInspectionAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        var activeCancellation = token;
        try
        {
            await Machine.RunManualMotionAsync(
                ActiveMotionGroup,
                async ct =>
                {
                    activeCancellation = ct;
                    if (await StopCameraLiveAsync() is not null)
                        return;
                    ct.ThrowIfCancellationRequested();
                    CameraError = null;
                    await action(ct);
                },
                token,
                ViewCancellation);
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching inspection failed. {0}", exception);
            if (!activeCancellation.IsCancellationRequested)
                CameraError = exception.Message;
        }
    }

    private Task<Exception?> StopCameraLiveAsync()
    {
        ToggleLiveViewCommand.Cancel();
        if (!_cameraStop.IsCompleted)
            return _cameraStop;

        lock (_liveImageGate)
        {
            LiveImage = null;
            _pendingLiveFrame = null;
        }

        _cameraStop = StopAsync();
        return _cameraStop;

        async Task<Exception?> StopAsync()
        {
            try
            {
                await _boltInspector.StopLiveViewAsync();
                return null;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Camera live view stop failed. {0}", exception);
                return exception;
            }
        }
    }

    private void OnLiveViewChanged()
    {
        Application.Current.Dispatcher.BeginInvoke(RefreshLiveView);
    }

    private void RefreshLiveView()
    {
        OnPropertyChanged(nameof(IsCameraLive));
        OnPropertyChanged(nameof(CameraError));
        if (!IsCameraLive)
        {
            lock (_liveImageGate)
            {
                LiveImage = null;
                _pendingLiveFrame = null;
            }
        }

        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImagesCommand.NotifyCanExecuteChanged();
        TeachImagePointCommand.NotifyCanExecuteChanged();
        TeachImageRegionCommand.NotifyCanExecuteChanged();
    }

    private async Task HandlePreviewFailureAsync(Exception exception)
    {
        System.Diagnostics.Trace.TraceError("Camera preview conversion failed. {0}", exception);
        await StopCameraLiveAsync();
        CameraError = exception.Message;
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
            _liveImageUpdateQueued = false;
        }
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        if (!IsCameraLive)
        {
            return;
        }

        lock (_liveImageGate)
        {
            _pendingLiveFrame = frame;
            if (_liveImageUpdateQueued)
            {
                return;
            }

            _liveImageUpdateQueued = true;
            _liveImageUpdate = Task.Run(UpdateLiveImagesAsync);
        }
    }

    private async Task UpdateLiveImagesAsync()
    {
        try
        {
            while (true)
            {
                ImageFrame frame;
                lock (_liveImageGate)
                {
                    if (_pendingLiveFrame is null)
                    {
                        _liveImageUpdateQueued = false;
                        return;
                    }

                    frame = _pendingLiveFrame;
                    _pendingLiveFrame = null;
                }

                var image = InspectionPreview.CreateBitmap(frame);
                // Frozen frames can cross threads; WPF marshals the scalar binding.
                lock (_liveImageGate)
                {
                    if (IsCameraLive && IsInspectionSelected)
                        LiveImage = image;
                }
            }
        }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(
                () => HandlePreviewFailureAsync(exception)).Task.Unwrap();
        }
    }

}
