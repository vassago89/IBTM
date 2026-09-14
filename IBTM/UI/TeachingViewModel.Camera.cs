using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class TeachingViewModel
{
    private bool _selectingFovTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrawFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(TeachFovRegionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    private bool _isMeasuring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    private ImageRuler? _ruler;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulerResolution))]
    [NotifyCanExecuteChangedFor(nameof(ApplyRulerResolutionCommand))]
    private double? _rulerMillimeters;

    public double? RulerResolution
    {
        get
        {
            if (Ruler is not { PixelLength: >= 1 } ruler || RulerMillimeters is not > 0)
                return null;
            var resolution = RulerMillimeters.Value / ruler.PixelLength;
            return double.IsFinite(resolution) && resolution > 0 ? resolution : null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMeasureImage))]
    private void MeasureImage(ImageRuler ruler)
    {
        if (CanMeasureImage(ruler) && ruler.PixelLength >= 1)
            Ruler = ruler;
    }

    private bool CanMeasureImage(ImageRuler ruler)
    {
        if (!IsInspectionSelected || !IsMeasuring || SelectedFov is not { } fov)
            return false;
        var bounds = new Rect(0, 0, fov.Image.PixelWidth, fov.Image.PixelHeight);
        return bounds.Contains(ruler.Start) && bounds.Contains(ruler.End);
    }

    [RelayCommand(CanExecute = nameof(CanApplyRulerResolution))]
    private async Task ApplyRulerResolutionAsync(CancellationToken cancellationToken)
    {
        if (!CanApplyRulerResolution())
            return;
        var resolution = RulerResolution!.Value;
        CameraError = null;
        try
        {
            await Machine.RunTeachingEditAsync(
                async token =>
                {
                    // Calculate every saved bolt ROI before changing the recipe. Capture XY stays fixed.
                    var positions = new List<(BoltPoint Bolt, AxisPosition? Position)>();
                    foreach (var bolt in RecipeEditor.Recipe.Pcb.BoltPoints)
                    {
                        var fov = CarrierImages.SingleOrDefault(image =>
                            !image.Metadata.IsBarcode
                            && image.Metadata.HeatSink == bolt.HeatSink
                            && image.Metadata.BoltNumber == bolt.Number);
                        if (fov?.Metadata.Region is not { } region)
                            continue;
                        if (!region.IsInside(fov.Image.PixelWidth, fov.Image.PixelHeight))
                            throw new InvalidOperationException($"Check the ROI of FOV {fov.Metadata.Number} before applying resolution.");
                        positions.Add((bolt, GetBoltCoordinates(fov, region, resolution)));
                    }
                    MillimetersPerPixel = resolution;
                    foreach (var (bolt, position) in positions)
                    {
                        bolt.X = position?.X;
                        bolt.Y = position?.Y;
                    }
                    RefreshPointPositions();
                    await RecipeEditor.SaveAsync(token);
                },
                cancellationToken,
                ViewCancellation);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || ViewCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CameraError = exception.Message;
        }
    }

    private bool CanApplyRulerResolution()
    {
        return CanEditInspectionRecipe && IsInspectionSelected && IsMeasuring
            && SelectedFov is not null && RulerResolution is not null
            && RecipeEditor.CanSave && CarrierImages.Count == RecipeEditor.Recipe.CarrierImages.Count;
    }

    [ObservableProperty]
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
        ApplyRulerResolutionCommand.Cancel();
        Ruler = null;
        RulerMillimeters = null;
        CaptureInspectionCommand.Cancel();
        ReinspectImageCommand.Cancel();
        var metadata = value?.Metadata;
        if (IsInspectionSelected && metadata is { Region: not null }
            && (metadata.IsBarcode || metadata.BoltNumber is not null))
        {
            _selectingFovTarget = true;
            try
            {
                SelectedPcb = metadata.HeatSink;
                SelectedPoint = FilteredPoints.FirstOrDefault(point => metadata.IsBarcode
                    ? point.Position.Target == TeachingTarget.DataMatrix
                    : point.Position.Bolt?.Number == metadata.BoltNumber);
            }
            finally
            {
                _selectingFovTarget = false;
            }
        }

        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        var bounds = metadata?.Region is { } region
            ? new Rect(region.X, region.Y, region.Width, region.Height)
            : (Rect?)null;
        if (FovRegion == bounds)
            RefreshSavedPreview();
        else
            FovRegion = bounds;
        OnPropertyChanged(nameof(FovRoiLabel));
        MeasureImageCommand.NotifyCanExecuteChanged();
        NotifyManualTeachingCommands();
    }

    partial void OnFovRegionChanged(Rect? value)
    {
        CaptureInspectionCommand.Cancel();
        ReinspectImageCommand.Cancel();
        ReadDataMatrixCommand.Cancel();
        DataMatrixResult = null;
        RefreshSavedPreview();
    }

    private void SelectFovForTeachingPoint()
    {
        var fov = IsInspectionSelected
            ? CarrierImages.FirstOrDefault(image => image.Metadata.HeatSink == SelectedPcb
                && (SelectedBarcode is not null
                    ? image.Metadata.IsBarcode
                    : IsBoltSelected && !image.Metadata.IsBarcode
                        && image.Metadata.BoltNumber == SelectedPoint!.BoltNumber))
            : null;
        if (fov is null && IsInspectionSelected)
        {
            // Keep a captured, unassigned image available when adding a new bolt.
            var drafts = CarrierImages.Where(image =>
                !image.Metadata.IsBarcode && image.Metadata.BoltNumber is null);
            fov = drafts.FirstOrDefault(image => image.Metadata.Number == SelectedFov?.Metadata.Number);
        }

        if (SelectedFov != fov)
            SelectedFov = fov;
        else
            RefreshSavedPreview();
    }

    private void RefreshSavedPreview()
    {
        Preview.Clear(SelectedBarcode, SelectedPoint?.Position.Bolt);
        if (!IsInspectionSelected || SelectedFov is not { } fov)
            return;
        try
        {
            var region = FovRegion is { Width: >= 1, Height: >= 1 } bounds
                ? new PixelRegion(
                    (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                    (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.Left),
                    (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Top))
                : null;
            Preview.SetSavedImage(fov.Image, region);
            CameraError = null;
        }
        catch (Exception exception)
        {
            CameraError = exception.Message;
        }
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
                    var frame = InspectionPreview.CreateFrame(fov.Image);
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
            var metadata = SelectedFov?.Metadata;
            var saved = metadata?.Region is { } region
                ? new Rect(region.X, region.Y, region.Width, region.Height)
                : (Rect?)null;
            if (FovRegion is not null && FovRegion != saved)
            {
                if (SelectedBarcode is null && SelectedPoint?.Position.Bolt is null)
                    return "ROI not saved · Add/select a bolt or select Data Matrix, then Apply ROI.";
                return "ROI not saved · Set the resolution and Apply ROI.";
            }
            if (metadata is { IsBarcode: true } barcode)
                return $"{barcode.HeatSink.GetDescription()} · Data Matrix · Drag to replace ROI";
            return metadata?.BoltNumber is { } number
                ? $"{metadata.HeatSink.GetDescription()} · Bolt {number} · Drag to replace ROI"
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
            && !IsMeasuring
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
                    var position = GetBoltCoordinates(fov, region, MillimetersPerPixel);
                    bolt.Point.X = position?.X;
                    bolt.Point.Y = position?.Y;
                }
                foreach (var tile in RecipeEditor.Recipe.CarrierImages)
                {
                    if (tile == fov.Metadata)
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
                OnSelectedFovChanged(SelectedFov);
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
            && !IsMeasuring
            && RecipeEditor.CanSave
            && SelectedFov is not null
            && (SelectedBarcode is not null
                || double.IsFinite(MillimetersPerPixel)
                    && MillimetersPerPixel > 0
                    && SelectedPoint?.Position.Bolt is not null)
            && (bounds.IsEmpty || bounds.Width >= 1 && bounds.Height >= 1);
    }

    private AxisPosition? GetBoltCoordinates(CarrierImageTileView fov, PixelRegion region, double resolution)
    {
        // Inspection can use capture XY without reference pins; fastening coordinates remain unknown.
        if (!_carrierReference.IsDefined)
            return null;
        var x = fov.Metadata.Center.X
            + (region.X + region.Width / 2.0 - fov.Image.PixelWidth / 2.0) * resolution;
        var y = fov.Metadata.Center.Y
            + (region.Y + region.Height / 2.0 - fov.Image.PixelHeight / 2.0) * resolution;
        return CarrierCoordinates.FromMachine(
            new AxisPosition { X = x, Y = y }, _carrierReference.UpperLeftLocatingPin!);
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
                && !CaptureInspectionCommand.IsRunning;
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
                var number = CarrierImages.Count == 0 ? 1 : CarrierImages.Max(tile => tile.Metadata.Number) + 1;
                var metadata = new CarrierImageTile { Number = number, Center = captured.Center };
                CarrierImageTileView[] images = [.. CarrierImages, new(metadata, image)];
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
                Preview.Clear(pcb, bolt);
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
            await Machine.RunTeachingEditAsync(
                Preview.InspectAsync,
                token,
                ViewCancellation);
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
        return CanEditInspectionRecipe
            && (IsBoltSelected || IsDataMatrixSelected)
            && Preview.HasImage && Preview.Region is not null;
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
        if (!PositionUpdatesActive)
            return;
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
