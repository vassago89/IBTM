using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class StationTeachingViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FovRoiLabel))]
    [NotifyCanExecuteChangedFor(nameof(DrawFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadDataMatrixCommand))]
    private CarrierImageTileView? _selectedFov;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FovRoiLabel))]
    [NotifyCanExecuteChangedFor(nameof(ReadDataMatrixCommand))]
    private Rect? _fovRegion;

    [ObservableProperty]
    private string? _dataMatrixResult;

    partial void OnSelectedFovChanged(CarrierImageTileView? value)
    {
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        FovRegion = value?.Region is { } region
            ? new Rect(region.X, region.Y, region.Width, region.Height)
            : null;
    }

    partial void OnFovRegionChanged(Rect? value)
    {
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
    }

    [RelayCommand(CanExecute = nameof(CanReadDataMatrix))]
    private async Task ReadDataMatrixAsync(CancellationToken cancellationToken)
    {
        var fov = SelectedFov!;
        var bounds = FovRegion!.Value;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ViewCancellation);
        DataMatrixResult = "Reading…";
        CameraError = null;
        try
        {
            var text = await Task.Run(
                () =>
                {
                    var image = fov.Image;
                    if (image.Format != PixelFormats.Bgr24)
                        image = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
                    var stride = image.PixelWidth * ImageFrame.ColorChannelCount;
                    var pixels = new byte[stride * image.PixelHeight];
                    image.CopyPixels(pixels, stride, 0);
                    var frame = new ImageFrame(image.PixelWidth, image.PixelHeight, stride, pixels);
                    var left = (int)Math.Floor(bounds.Left);
                    var top = (int)Math.Floor(bounds.Top);
                    var region = new PixelRegion(
                        left, top,
                        (int)Math.Ceiling(bounds.Right) - left,
                        (int)Math.Ceiling(bounds.Bottom) - top);
                    return DataMatrixReader.Read(frame, region);
                },
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DataMatrixResult = string.IsNullOrEmpty(text) ? "Not Read" : text;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DataMatrixResult = null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Teaching Data Matrix read failed. {0}", exception);
            DataMatrixResult = null;
            CameraError = exception.Message;
        }
    }

    private bool CanReadDataMatrix()
    {
        return IsInspectionSelected
            && SelectedBarcode is not null
            && SelectedFov is { } fov
            && FovRegion is { Width: >= 1, Height: >= 1 } bounds
            && bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= fov.Image.PixelWidth
            && bounds.Bottom <= fov.Image.PixelHeight;
    }

    public string FovRoiLabel
    {
        get
        {
            var saved = SelectedFov?.Region is { } region
                ? new Rect(region.X, region.Y, region.Width, region.Height)
                : (Rect?)null;
            if (FovRegion is not null && FovRegion != saved)
            {
                if (SelectedBarcode is null && SelectedPoint?.Position.Bolt is null)
                    return "ROI not saved · Add/select a bolt or select Data Matrix, then Apply ROI.";
                if (SelectedBarcode is null && !_carrierReference.IsDefined)
                    return "ROI not saved · Teach both backup plate reference pins, then Apply ROI.";
                return "ROI not saved · Set the resolution and Apply ROI.";
            }
            if (SelectedFov is { IsBarcode: true } barcode)
                return $"{barcode.HeatSink.GetDescription()} · Data Matrix · Drag to replace ROI";
            return SelectedFov?.BoltNumber is { } number
                ? $"{SelectedFov.HeatSink.GetDescription()} · Bolt {number} · Drag to replace ROI"
                : "Drag one ROI. Select a bolt or Data Matrix to save it.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDrawFovRegion))]
    private async Task DrawFovRegionAsync(Rect bounds)
    {
        FovRegion = bounds;
        if (CanTeachFovRegion(bounds))
            await TeachFovRegionAsync(bounds);
    }

    private bool CanDrawFovRegion(Rect bounds)
    {
        return CanEditInspectionRecipe
            && RecipeEditor.CanSave
            && SelectedFov is not null
            && (bounds.IsEmpty || bounds.Width >= 1 && bounds.Height >= 1);
    }

    [RelayCommand(CanExecute = nameof(CanTeachFovRegion))]
    private async Task TeachFovRegionAsync(Rect bounds)
    {
        var fov = SelectedFov!;
        var point = SelectedPoint!;
        var barcode = SelectedBarcode;
        var bolt = point.Position.Bolt;
        var pcb = SelectedPcb;
        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var region = new PixelRegion(left, top,
            (int)Math.Ceiling(bounds.Right) - left, (int)Math.Ceiling(bounds.Bottom) - top);
        if (!region.IsInside(fov.Image.PixelWidth, fov.Image.PixelHeight))
            return;

        await Machine.RunTeachingEditAsync(
            async token =>
            {
                if (bolt is not null)
                {
                    var x = fov.Center.X
                        + (region.X + region.Width / 2.0 - fov.Image.PixelWidth / 2.0) * MillimetersPerPixel;
                    var y = fov.Center.Y
                        + (region.Y + region.Height / 2.0 - fov.Image.PixelHeight / 2.0) * MillimetersPerPixel;
                    point.Teach(x, y, 0);
                    point.Apply();
                }
                foreach (var tile in RecipeEditor.Recipe.CarrierImages)
                {
                    if (tile.Number == fov.Number)
                    {
                        tile.Region = region;
                        tile.BoltNumber = bolt?.Number;
                        tile.IsBarcode = barcode is not null;
                        tile.HeatSink = pcb;
                    }
                    else if (tile.HeatSink == pcb
                        && (barcode is not null
                            ? tile.IsBarcode
                            : !tile.IsBarcode && tile.BoltNumber == bolt!.Number))
                    {
                        tile.Region = null;
                        tile.BoltNumber = null;
                        tile.IsBarcode = false;
                    }
                }
                CarrierImages = CarrierImages.Select(image =>
                {
                    var tile = RecipeEditor.Recipe.CarrierImages.Single(tile => tile.Number == image.Number);
                    return image with
                    {
                        Region = tile.Region,
                        BoltNumber = tile.BoltNumber,
                        IsBarcode = tile.IsBarcode,
                        HeatSink = tile.HeatSink,
                    };
                }).ToArray();
                RefreshPointPositions();
                await RecipeEditor.SaveAsync(token);
                NotifyManualTeachingCommands();
            },
            CancellationToken.None,
            ViewCancellation);

        if (barcode is not null && ReadDataMatrixCommand.CanExecute(null))
            await ReadDataMatrixCommand.ExecuteAsync(null);
    }

    private bool CanTeachFovRegion(Rect bounds)
    {
        return CanEditInspectionRecipe
            && RecipeEditor.CanSave
            && SelectedFov is not null
            && (SelectedBarcode is not null
                || double.IsFinite(MillimetersPerPixel)
                    && MillimetersPerPixel > 0
                    && _carrierReference.IsDefined
                    && SelectedPoint?.Position.Bolt is not null)
            && (bounds.IsEmpty || bounds.Width >= 1 && bounds.Height >= 1);
    }

    private readonly object _liveImageGate = new();
    private ImageFrame? _pendingLiveFrame;
    private bool _liveImageUpdateQueued;
    private Task _liveImageUpdate = Task.CompletedTask;
    private Task _cameraStop = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanToggleLiveView))]
    private async Task ToggleLiveViewAsync(CancellationToken cancellationToken)
    {
        if (!CanToggleLiveView())
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, ViewCancellation);
        try
        {
            if (Inspector.IsLiveView)
            {
                await StopCameraLiveAsync();
                return;
            }

            Preview.Clear(SelectedBarcode);
            CameraError = null;
            SelectedCameraTab = 0;
            await Inspector.StartLiveViewAsync(cancellation.Token);
            if (!_state.ManualMode || !IsInspectionSelected)
                await StopCameraLiveAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Camera live view operation failed. {0}", exception);
        }
    }

    private bool CanToggleLiveView()
    {
        return Inspector.IsLiveView
            || IsInspectionSelected
                && _state.ManualMode
                && !CaptureCarrierImageCommand.IsRunning
                && !CaptureInspectionCommand.IsRunning
                && !CollectBoltImagesCommand.IsRunning;
    }

    private void OnInspectionCommandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            ToggleLiveViewCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCaptureCarrierImage))]
    private Task CaptureCarrierImageAsync(CancellationToken cancellationToken)
    {
        return RunInspectionAsync(
            async token =>
            {
                await _recipeImageUpdate;
                token.ThrowIfCancellationRequested();
                if (CarrierImages.Count != RecipeEditor.Recipe.CarrierImages.Count)
                    throw new InvalidOperationException("Load the saved FOV images before adding another image.");
                var captured = await Inspector.CaptureCarrierImageAsync(token);
                var image = await Task.Run(
                    () => InspectionPreview.CreateBitmap(captured.Frame),
                    token);
                var number = CarrierImages.Count == 0 ? 1 : CarrierImages.Max(tile => tile.Number) + 1;
                CarrierImageTileView[] images = [.. CarrierImages, new(number, captured.Center, image)];
                if (await RecipeEditor.SaveCarrierImagesAsync(images, token))
                {
                    CarrierImages = images;
                    SelectedFov = images[^1];
                }
            },
            cancellationToken,
            stopLiveView: false);
    }

    private bool CanCaptureCarrierImage()
    {
        return IsInspectionSelected
            && Machine.CanUseManualMotion(ActiveMotionGroup, live: false)
            && Motion.Axes.Values.All(axis => axis.State is { InMotion: false, InPosition: true })
            && MillimetersPerPixel > 0
            && RecipeEditor.CanSave;
    }

    [RelayCommand(CanExecute = nameof(CanClearCarrierImages))]
    private Task ClearCarrierImagesAsync(CancellationToken cancellationToken)
    {
        return Machine.RunTeachingEditAsync(
            async token =>
            {
                await _recipeImageUpdate;
                if (await RecipeEditor.SaveCarrierImagesAsync([], token))
                    CarrierImages = [];
            },
            cancellationToken,
            ViewCancellation);
    }

    private bool CanClearCarrierImages()
    {
        return IsInspectionSelected && CanEditTeaching && HasCarrierImages && RecipeEditor.CanSave;
    }

    [RelayCommand(CanExecute = nameof(CanCaptureInspection))]
    private Task CaptureInspectionAsync(CancellationToken token)
    {
        SelectedCameraTab = 0;
        return RunInspectionAsync(
            async ct =>
            {
                var pcb = SelectedBarcode;
                var bolt = SelectedPoint!.Position.Bolt;
                var region = pcb is { } target
                    ? Inspector.GetBarcodeFov(target).Region
                    : Inspector.GetFov(bolt!).Region;
                Preview.Clear(pcb);
                var frame = pcb is { } barcode
                    ? await Inspector.CaptureBarcodeAsync(barcode, ct)
                    : await Inspector.CaptureAsync(bolt!, ct);
                await Preview.SetImageAsync(frame, ct, region);
                await Preview.InspectAsync(ct);
            },
            token);
    }

    private bool CanCaptureInspection()
    {
        return IsInspectionSelected
            && CanMoveToPoint()
            && (SelectedBarcode is { } pcb
                ? Inspector.HasBarcodeRegion(pcb)
                : SelectedPoint?.Position.Bolt is { } bolt && Inspector.HasPosition(bolt));
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
                    var frame = await Inspector.CaptureAsync(point, ct);
                    await Task.Run(
                        () => _trainingStore.AddImage(
                            $"{point.HeatSink.GetDescription()} · Bolt {point.Number}",
                            BoltImageInput.Create(frame, Inspector.GetFov(point).Region!),
                            IBoltRecessSegmenter.InputSize),
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
                .All(Inspector.HasPosition);
    }

    private async Task RunInspectionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken token,
        bool stopLiveView = true)
    {
        var activeCancellation = token;
        try
        {
            await Machine.RunManualMotionAsync(
                ActiveMotionGroup,
                async ct =>
                {
                    activeCancellation = ct;
                    if (stopLiveView)
                        await StopCameraLiveAsync();
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

    private Task StopCameraLiveAsync()
    {
        ToggleLiveViewCommand.Cancel();
        if (!_cameraStop.IsCompleted)
            return _cameraStop;

        lock (_liveImageGate)
        {
            LiveImage = null;
            _pendingLiveFrame = null;
        }

        _cameraStop = Inspector.StopLiveViewAsync();
        return _cameraStop;
    }

    private async Task RequestCameraStopAsync()
    {
        try
        {
            await StopCameraLiveAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Camera live view stop failed. {0}", exception);
        }
    }

    private void OnLiveViewChanged()
    {
        Application.Current.Dispatcher.BeginInvoke(RefreshLiveView);
    }

    private void RefreshLiveView()
    {
        OnPropertyChanged(nameof(Inspector));
        OnPropertyChanged(nameof(CameraError));
        if (!Inspector.IsLiveView)
        {
            lock (_liveImageGate)
            {
                LiveImage = null;
                _pendingLiveFrame = null;
            }
        }

        ToggleLiveViewCommand.NotifyCanExecuteChanged();
        CaptureCarrierImageCommand.NotifyCanExecuteChanged();
    }

    private async Task HandlePreviewFailureAsync(Exception exception)
    {
        System.Diagnostics.Trace.TraceError("Camera preview conversion failed. {0}", exception);
        await RequestCameraStopAsync();
        CameraError = exception.Message;
        lock (_liveImageGate)
        {
            _pendingLiveFrame = null;
            _liveImageUpdateQueued = false;
        }
    }

    private void UpdateLiveImage(ImageFrame frame)
    {
        if (!Inspector.IsLiveView)
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
                    if (Inspector.IsLiveView && IsInspectionSelected)
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
