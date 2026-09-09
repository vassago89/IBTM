using System;
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

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private void ToggleLiveView()
    {
        if (IsCameraLive)
        {
            StopCamera();
        }
        else
        {
            if (!_state.ManualOutputsEnabled) return;
            CameraError = null;
            Preview.Clear(SelectedBarcode);
            IsCameraLive = true;
            try
            {
                _boltInspector.StartLiveView();
            }
            catch (Exception exception)
            {
                HandleLiveViewFailure(exception);
            }
        }
    }

    private bool CanToggleLiveView() =>
        IsInspectionSelected
        && (IsCameraLive || _state.Display.ManualOutputsEnabled);

    [RelayCommand(CanExecute = nameof(CanCaptureCarrierImages))]
    private async Task CaptureCarrierImagesAsync(
        CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await RunInspectionAsync(async token =>
            {
                _recipeImageCancellation.Cancel();
                await _recipeImageUpdate;
                token.ThrowIfCancellationRequested();
                Preview.Clear(SelectedBarcode);
                CarrierImages = [];
                var captured = await _boltInspector.CaptureCarrierImagesAsync(token);
                var images = await Task.Run(
                    () => captured
                        .Select((image, index) => new CarrierImageTileView(
                            index + 1,
                            image.Center,
                            InspectionPreview.CreateBitmap(image.Frame)))
                        .ToArray(),
                    token);

                token.ThrowIfCancellationRequested();
                CarrierImages = images;
                completed = await RecipeEditor.SaveCarrierImagesAsync(images, token);
                if (!completed) return;
                token.ThrowIfCancellationRequested();
                SelectedPoint = NextTeachingPoint() ?? FilteredPoints.FirstOrDefault(point =>
                    point.Target == TeachingTarget.BoltReference);
            }, cancellationToken);
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

    private bool CanCaptureCarrierImages() =>
        IsInspectionSelected
        && _inspectionGantry.CanMove
        && _carrierReference.IsDefined
        && MillimetersPerPixel > 0
        && ScanOverlap >= 0
        && ScanOverlap < _boltInspector.FieldOfView.Width
        && ScanOverlap < _boltInspector.FieldOfView.Height
        && RecipeEditor.CanSave
        && CanUseCurrentHandler();

    [RelayCommand(CanExecute = nameof(CanTeachImagePoint))]
    private async Task TeachImagePointAsync(Point imagePoint)
    {
        if (!CanEditRecipe()) return;
        if (SelectedPoint!.Target == TeachingTarget.BoltReference)
            SelectedPcb = FindPcb(new Rect(imagePoint, new Size()))!.Value;
        var point = SelectedPoint!;
        point.Teach(imagePoint.X, imagePoint.Y, 0);
        point.Apply();
        RefreshPointPositions();
        await RecipeEditor.SaveAsync(ViewCancellation);
        NotifyManualTeachingCommands();
    }

    private bool CanTeachImagePoint(Point point) =>
        CanEditInspectionRecipe
        && RecipeEditor.CanSave
        && HasCarrierImages
        && !IsCameraLive
        && _carrierReference.IsDefined
        && (SelectedPoint?.Target == TeachingTarget.BoltReference
                && FindPcb(new Rect(point, new Size())) is not null
            || SelectedPoint?.Target == TeachingTarget.PcbRegion && SelectedPcb == HeatSinkSlot.HeatSink2
                && CurrentRecipe.Pcb.GetRegion(HeatSinkSlot.HeatSink1) is not null);

    [RelayCommand(CanExecute = nameof(CanTeachImageRegion))]
    private async Task TeachImageRegionAsync(Rect bounds)
    {
        if (!CanEditRecipe()) return;
        CameraError = null;
        var pin = _carrierReference.UpperLeftLocatingPin!;
        var layout = CurrentRecipe.Pcb;
        if (SelectedPoint!.Target == TeachingTarget.PcbRegion)
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
            layout.DataMatrix = new(bounds.X - pin.X - origin.X, bounds.Y - pin.Y - origin.Y,
                bounds.Width, bounds.Height);
        }
        RefreshPointPositions();
        await RecipeEditor.SaveAsync(ViewCancellation);
        NotifyManualTeachingCommands();
    }

    private bool CanTeachImageRegion(Rect bounds) =>
        CanEditInspectionRecipe && RecipeEditor.CanSave && HasCarrierImages && !IsCameraLive && _carrierReference.IsDefined
        && (SelectedPoint?.Target == TeachingTarget.PcbRegion && SelectedPcb == HeatSinkSlot.HeatSink1
            || SelectedBarcode is not null && (bounds.IsEmpty
                ? Enum.GetValues<HeatSinkSlot>().Any(pcb => CurrentRecipe.Pcb.GetRegion(pcb) is not null)
                : FindPcb(bounds) is not null));

    private HeatSinkSlot? FindPcb(Rect bounds)
    {
        var pin = _carrierReference.UpperLeftLocatingPin!;
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            if (CurrentRecipe.Pcb.GetRegion(pcb) is { } region
                && new Rect(region.X + pin.X, region.Y + pin.Y, region.Width, region.Height).Contains(bounds))
                return pcb;
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanCaptureInspection))]
    private Task CaptureInspectionAsync(CancellationToken token) => RunInspectionAsync(async ct =>
    {
        Preview.Clear(SelectedBarcode);
        var frame = SelectedBarcode is { } pcb
            ? await _boltInspector.CaptureBarcodeAsync(pcb, ct)
            : await _boltInspector.CaptureAsync(SelectedPoint!.Position.Bolt!, ct);
        await Preview.SetImageAsync(frame, ct);
        await Preview.InspectAsync(ct);
    }, token);

    private bool CanCaptureInspection() => IsInspectionSelected && CanMoveToPoint()
        && (SelectedBarcode is { } pcb ? _boltInspector.HasBarcodeRegion(pcb)
            : SelectedPoint?.Target == TeachingTarget.BoltReference);

    [RelayCommand(CanExecute = nameof(CanReinspectImage))]
    private Task ReinspectImageAsync(CancellationToken token) => RunInspectionAsync(Preview.InspectAsync, token);

    private bool CanReinspectImage() => CanEditInspectionRecipe && Preview.HasImage;

    [RelayCommand(CanExecute = nameof(CanCollectBoltImages))]
    private Task CollectBoltImagesAsync(CancellationToken token) => RunInspectionAsync(async ct =>
    {
        foreach (var point in CurrentRecipe.Pcb.GetBolts()
            .Where(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
            .OrderBy(point => point.HeatSink).ThenBy(point => point.Number))
        {
            var frame = await _boltInspector.CaptureAsync(point, ct);
            await Task.Run(() => _trainingStore.AddImage(
                $"{point.HeatSink.GetDescription()} · Bolt {point.Number}", frame, InspectionRecipe.RegionSizePixels), ct);
        }
    }, token);

    private bool CanCollectBoltImages() => IsInspectionSelected && CanUseCurrentHandler() && _inspectionGantry.CanMove
        && CurrentRecipe.Pcb.GetBolts().Any(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
        && CurrentRecipe.Pcb.GetBolts().Where(point => _inspectionWork.HeatSinkPresent(point.HeatSink))
            .All(_boltInspector.HasPosition);

    private async Task RunInspectionAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        var activeCancellation = token;
        try
        {
            await RunMotionAsync(async ct =>
            {
                activeCancellation = ct;
                CameraError = null;
                if (IsCameraLive) StopCamera();
                if (CameraError is not null) return;
                await action(ct);
            }, token);
        }
        catch (Exception exception)
        {
            if (!activeCancellation.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceError("Teaching inspection failed. {0}", exception);
                CameraError = exception.Message;
            }
        }
    }

    private void StopCamera()
    {
        IsCameraLive = false;
        LiveImage = null;
        try
        {
            _boltInspector.StopLiveView();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Camera live view stop failed. {0}", exception);
            CameraError = exception.Message;
        }
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
        }
    }

    private void OnLiveViewFailed(Exception exception) =>
        Application.Current.Dispatcher.BeginInvoke(
            () => HandleLiveViewFailure(exception));

    private void HandleLiveViewFailure(Exception exception)
    {
        System.Diagnostics.Trace.TraceError("Camera live view failed. {0}", exception);
        if (!IsCameraLive)
        {
            return;
        }

        StopCamera();
        CameraError = exception.Message;
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
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (IsCameraLive && IsInspectionSelected)
                    {
                        LiveImage = image;
                    }
                });
            }
        }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HandleLiveViewFailure(exception);
                lock (_liveImageGate)
                {
                    _pendingLiveFrame = null;
                    _liveImageUpdateQueued = false;
                }
            });
        }
    }

}
